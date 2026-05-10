using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
        var protector = NewProtector();
        await using (var ctx = _db.NewContextWithAudit(NewAuditInterceptor()))
        {
            var svc = new SystemSettingsService(ctx, protector, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
            await svc.SaveAsync(new SystemSettingsInput(
                SmtpHost: "first.example.com",
                SmtpPort: 587,
                SmtpUser: "u",
                SmtpPassword: "first-secret",
                SmtpFrom: "noreply@example.com",
                SmtpUseStartTls: true,
                BannerText: null,
                DefaultSignupAutoApprove: false));
        }
        // Second save: triggers an Updated audit row whose snapshot
        // captures the *previous* row state (which holds ciphertext for
        // first-secret).
        await using (var ctx = _db.NewContextWithAudit(NewAuditInterceptor()))
        {
            var svc = new SystemSettingsService(ctx, protector, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
            await svc.SaveAsync(new SystemSettingsInput(
                SmtpHost: "second.example.com",
                SmtpPort: 587,
                SmtpUser: "u",
                SmtpPassword: "second-secret",
                SmtpFrom: "noreply@example.com",
                SmtpUseStartTls: true,
                BannerText: null,
                DefaultSignupAutoApprove: false));
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

    private static IDataProtectionProvider NewProtector()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }

    private static Data.AuditInterceptor NewAuditInterceptor() =>
        new(new Microsoft.AspNetCore.Http.HttpContextAccessor());
}
