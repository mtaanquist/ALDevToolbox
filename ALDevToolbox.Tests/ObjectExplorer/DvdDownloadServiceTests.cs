using System.Diagnostics;
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

/// <summary>
/// Tests for the pure stream-copy helper — kept in their own class so they
/// don't pay the TestDb / Testcontainers fixture cost the validation tests
/// above need.
/// </summary>
public sealed class DvdDownloadServiceCopyTests
{
    [Fact]
    public async Task Copy_throws_TimeoutException_when_the_source_stalls()
    {
        // A CDN that returns headers + a few bytes and then stops sending
        // used to hang the worker forever — HttpClient.Timeout's
        // ResponseHeadersRead semantics don't reliably cover the body. The
        // per-read idle window should abort within the configured timeout.
        using var src = new StallingStream(new byte[] { 1, 2, 3, 4 });
        using var dst = new MemoryStream();
        var sw = Stopwatch.StartNew();

        var act = async () => await DvdDownloadService.CopyWithCapAsync(
            src, dst, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        // Bound generously — CancelAfter is wall-clock plus CI noise.
        // The point of the assertion is: not 20 minutes.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        // The initial bytes still made it through before the stall.
        dst.ToArray().Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task Copy_respects_caller_cancellation_over_idle_timeout()
    {
        // Worker shutdown cancels the caller's token. That must propagate
        // as OperationCanceledException, NOT get repackaged as a
        // TimeoutException — otherwise clean shutdown would log a
        // misleading "stalled" warning every time.
        using var src = new StallingStream(Array.Empty<byte>());
        using var dst = new MemoryStream();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var act = async () => await DvdDownloadService.CopyWithCapAsync(
            src, dst, TimeSpan.FromSeconds(30), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Returns the initial chunk on the first ReadAsync, then stalls
    /// indefinitely until the caller's CancellationToken fires — models a
    /// CDN edge that opens the body and stops sending bytes.
    /// </summary>
    private sealed class StallingStream : Stream
    {
        private readonly byte[] _initialChunk;
        private int _pos;

        public StallingStream(byte[] initialChunk) => _initialChunk = initialChunk;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos < _initialChunk.Length)
            {
                var toCopy = Math.Min(buffer.Length, _initialChunk.Length - _pos);
                _initialChunk.AsSpan(_pos, toCopy).CopyTo(buffer.Span);
                _pos += toCopy;
                return ValueTask.FromResult(toCopy);
            }
            // Stall: complete only on cancellation. TaskCompletionSource
            // mirrors what a network stream does when the peer goes silent.
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(static state => ((TaskCompletionSource<int>)state!).TrySetCanceled(), tcs);
            return new ValueTask<int>(tcs.Task);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
