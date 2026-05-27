using System.Net.Http;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Validation tests for <see cref="DvdDownloadService"/>. These cover the
/// user-correctable rejections that fire before any network call — blank URL,
/// non-https scheme, and a host that isn't on the SiteAdmin allow-list. The
/// happy download path needs a real server and the SSRF behaviour is covered
/// by SsrfGuard's own tests, so neither is re-exercised here.
/// </summary>
public sealed class DvdDownloadServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Rejects_blank_url()
    {
        var svc = NewService();
        var act = async () => await svc.DownloadToTempAsync("   ");
        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("DvdUrl");
    }

    [Fact]
    public async Task Rejects_non_https_url()
    {
        var svc = NewService();
        await SetAllowlistAsync("download.microsoft.com");
        var act = async () => await svc.DownloadToTempAsync("http://download.microsoft.com/x.zip");
        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("DvdUrl");
    }

    [Fact]
    public async Task Rejects_host_not_on_allowlist()
    {
        var svc = NewService();
        // No allow-list configured → no host permitted.
        var act = async () => await svc.DownloadToTempAsync("https://evil.example.com/x.zip");
        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("DvdUrl");
    }

    private async Task SetAllowlistAsync(string hosts)
    {
        await using var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor());
        var settings = new SystemSettingsService(ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
        await settings.SaveAsync(new SystemSettingsInput(
            SmtpHost: null, SmtpPort: null, SmtpUser: null,
            SmtpPassword: null, ClearSmtpPassword: false,
            SmtpFrom: null, SmtpUseStartTls: null, BannerText: null,
            DefaultSignupAutoApprove: false,
            BackupScheduleEnabled: true,
            BackupScheduleTimeUtc: new TimeOnly(2, 0),
            BackupRetentionCount: 14,
            PerTenantBackupRetentionCount: 30,
            DefaultStorageQuotaMb: null,
            IndexSizeMultiplier: 0.5m,
            McpEnabled: false,
            SignupEmailDomainAllowlist: null,
            ReleaseDownloadDomainAllowlist: hosts));
    }

    private DvdDownloadService NewService()
    {
        var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor());
        var settings = new SystemSettingsService(ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
        return new DvdDownloadService(new ThrowingHttpClientFactory(), settings, NullLogger<DvdDownloadService>.Instance);
    }

    // The validation tests never reach the network; if CreateClient is called
    // the test has a bug, so fail loudly rather than silently hitting a server.
    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("Validation should reject before any HTTP call.");
    }
}
