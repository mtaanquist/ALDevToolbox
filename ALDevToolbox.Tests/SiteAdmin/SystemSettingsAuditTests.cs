using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Audit-redaction contract for <see cref="SystemSettings"/>: writes to the
/// SMTP password column produce an audit row whose snapshot stores a fixed
/// sentinel rather than the encrypted blob, so the audit log never carries
/// ciphertext history.
/// </summary>
public sealed class SystemSettingsAuditTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Save_writes_audit_row_with_redacted_smtp_password()
    {
        var protector = _db.DataProtectionProvider;
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            var svc = new SystemSettingsService(ctx, protector, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
            await svc.SaveAsync(NewInput("first.example.com", "first-secret"));
        }
        // Second save: triggers an Updated audit row whose snapshot
        // captures the *previous* row state (which holds ciphertext for
        // first-secret).
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            var svc = new SystemSettingsService(ctx, protector, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
            await svc.SaveAsync(NewInput("second.example.com", "second-secret"));
        }

        await using var read = _db.NewContext();
        var rows = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.SystemSettings)
            .ToListAsync();
        rows.Should().NotBeEmpty();
        // No snapshot should ever contain the plaintext password.
        foreach (var row in rows.Where(r => r.SnapshotJson is not null))
        {
            row.SnapshotJson.Should().NotContain("first-secret");
            row.SnapshotJson.Should().NotContain("second-secret");
        }
        // At least one snapshot captured a non-null password column — the
        // second save's "before" view — and that must read "[redacted]"
        // rather than the ciphertext.
        rows.Where(r => r.SnapshotJson is not null && r.SnapshotJson.Contains("[redacted]"))
            .Should().NotBeEmpty(
                "the SMTP password column is replaced with a fixed sentinel before snapshotting");
    }

    private static SystemSettingsInput NewInput(string host, string password) => new(
        SmtpHost: host,
        SmtpPort: 587,
        SmtpUser: "u",
        SmtpPassword: password,
        ClearSmtpPassword: false,
        SmtpFrom: "noreply@example.com",
        SmtpFromName: null,
        SmtpUseStartTls: true,
        BannerText: null,
        DefaultSignupAutoApprove: false,
        BackupScheduleEnabled: true,
        BackupScheduleTimeUtc: new TimeOnly(2, 0),
        BackupRetentionCount: 14,
        PerTenantBackupRetentionCount: 30,
        DefaultStorageQuotaMb: null,
        IndexSizeMultiplier: 0.5m,
        McpEnabled: false,
        SignupEmailDomainAllowlist: null,
        ReleaseDownloadDomainAllowlist: null);
}
