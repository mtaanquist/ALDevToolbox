using System.Diagnostics;

namespace ALDevToolbox.Services;

/// <summary>
/// Hosted service that refreshes the per-organisation storage snapshots in
/// <c>organization_usage_snapshots</c> every <see cref="RunInterval"/>.
/// Computing usage live means a sequential <c>COUNT(*)</c> over every tenanted
/// table — hundreds of milliseconds on a populated tenant — which used to run
/// on every authenticated navigation because the sidebar <c>StorageBar</c>
/// computed live. Moving the work to this background pass lets the bar (and the
/// SiteAdmin storage page) read a cached row instead.
///
/// <para>
/// Modelled after <see cref="ObjectExplorer.ObjectExplorerVacuumScheduler"/>:
/// polls every minute, ticks the heartbeat each poll, and fires once
/// <see cref="RunInterval"/> has elapsed since the last success. Polling (rather
/// than a single long <c>Task.Delay</c>) keeps the heartbeat fresh and means a
/// missed slot after a restart is caught on the next minute. The first pass
/// runs ~20s after startup so the figures are populated promptly without
/// racing the startup migrations.
/// </para>
///
/// <para>
/// Same opt-out shape as the other schedulers: set
/// <c>DISABLE_USAGE_SNAPSHOT_SCHEDULER=1</c> to keep the timer from starting
/// (tests, CI). The display surfaces degrade gracefully when no snapshot
/// exists yet — the bar simply hides.
/// </para>
/// </summary>
public sealed class UsageSnapshotScheduler : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RunInterval = TimeSpan.FromMinutes(15);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<UsageSnapshotScheduler> _logger;
    private DateTimeOffset? _lastRunUtc;
    private readonly WorkerHeartbeat _heartbeat;

    public UsageSnapshotScheduler(
        IServiceProvider services,
        TimeProvider clock,
        ILogger<UsageSnapshotScheduler> logger,
        WorkerHeartbeatRegistry heartbeats)
    {
        _services = services;
        _clock = clock;
        _logger = logger;
        // Poll every minute, stale at 5 (same as the vacuum scheduler). A
        // single recompute sweep is short; a 10-minute active ceiling is plenty.
        _heartbeat = heartbeats.Register(nameof(UsageSnapshotScheduler),
            maxActiveDuration: TimeSpan.FromMinutes(10),
            maxIdleSilence: TimeSpan.FromMinutes(5));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Defer a few seconds so startup migrations have settled before we
        // open another scope against the same pool.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            _heartbeat.Tick();
            try
            {
                _heartbeat.BeginActive();
                try { await TickAsync(stoppingToken); }
                finally { _heartbeat.EndActive(); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "UsageSnapshotScheduler tick threw; will retry on the next poll.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_lastRunUtc is { } last && now - last < RunInterval) return;

        var sw = Stopwatch.StartNew();
        await using var scope = _services.CreateAsyncScope();
        var usage = scope.ServiceProvider.GetRequiredService<DatabaseUsageService>();
        await usage.RecomputeSnapshotsAsync(ct);

        _lastRunUtc = now;
        _logger.LogDebug("Usage snapshot recompute finished in {ElapsedMs}ms.", sw.ElapsedMilliseconds);
    }
}
