using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Services.BcQuality;

/// <summary>
/// Keeps the mirrored BCQuality knowledge base current: one ingest shortly
/// after startup when the mirror is empty or stale, and a daily refresh
/// thereafter. Refresh policy is specified in <c>.design/bcquality.md</c>.
///
/// <para>
/// Modelled on <see cref="UsageSnapshotScheduler"/>: poll on a short interval,
/// tick the heartbeat each poll, and do the work only once
/// <see cref="RefreshInterval"/> has elapsed since the last <em>successful</em>
/// ingest — which is read from the database, not from a field, so a restart
/// does not re-clone and a missed slot is caught on the next poll. In-process
/// and on the sanctioned background-worker path, so the "no external services"
/// fence holds: no broker, no cron container.
/// </para>
///
/// <para>
/// Set <c>DISABLE_BCQUALITY_REFRESH=1</c> to keep the timer from starting
/// (tests, CI, offline hosts). With no refresh the MCP tools simply report an
/// empty knowledge base rather than failing.
/// </para>
/// </summary>
public sealed class BcQualityRefreshScheduler : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Daily. BCQuality is a prose knowledge base that gains a handful of
    /// articles a week; polling harder would only spend GitHub's bandwidth.
    /// </summary>
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// How long to wait after a failed attempt before trying again, so a
    /// GitHub outage does not turn into a clone every five minutes.
    /// </summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<BcQualityRefreshScheduler> _logger;
    private readonly WorkerHeartbeat _heartbeat;

    public BcQualityRefreshScheduler(
        IServiceProvider services,
        TimeProvider clock,
        ILogger<BcQualityRefreshScheduler> logger,
        WorkerHeartbeatRegistry heartbeats)
    {
        _services = services;
        _clock = clock;
        _logger = logger;
        // A full ingest is a shallow clone plus ~250 upserts — seconds, not
        // minutes. 15 minutes of active time is a wedged-job ceiling, and a
        // 15-minute idle ceiling matches the 5-minute poll with slack.
        _heartbeat = heartbeats.Register(nameof(BcQualityRefreshScheduler),
            maxActiveDuration: TimeSpan.FromMinutes(15),
            maxIdleSilence: TimeSpan.FromMinutes(15));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup migrations settle before opening another scope, and
        // before the first-run ingest competes with the seed for the pool.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
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
                // The service already stamped the failure on the provenance
                // row; this keeps one bad refresh from killing the loop.
                _logger.LogWarning(ex, "BCQuality refresh tick threw; will retry on a later poll.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var ingest = scope.ServiceProvider.GetRequiredService<BcQualityIngestService>();

        var state = await ingest.GetStateAsync(ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        if (!IsDue(state, now)) return;

        try
        {
            var result = await ingest.RefreshAsync(ct);
            _logger.LogInformation(
                "BCQuality mirror refreshed to {CommitSha}: {Total} articles ({Added} new, {Updated} changed, {Pruned} removed).",
                result.CommitSha, result.Total, result.Added, result.Updated, result.Pruned);
        }
        catch (PlanValidationException ex)
        {
            // A checkout that yields no articles is a refusal, not a crash —
            // the previous mirror is intact and the next poll retries.
            _logger.LogWarning("BCQuality refresh refused the checkout: {Errors}.",
                string.Join("; ", ex.Errors.Select(kv => kv.Key + ": " + kv.Value)));
        }
    }

    /// <summary>
    /// First run when nothing has ever been ingested; then daily. A failed
    /// attempt backs off for <see cref="RetryAfterFailure"/> so an outage does
    /// not turn the poll interval into a clone interval.
    /// </summary>
    internal static bool IsDue(Domain.Entities.BcQualityIngestState? state, DateTime nowUtc)
    {
        if (state?.LastSuccessAt is not { } lastSuccess)
        {
            return state?.LastAttemptAt is not { } firstAttempt
                || nowUtc - firstAttempt >= RetryAfterFailure;
        }
        if (nowUtc - lastSuccess < RefreshInterval) return false;
        if (state.LastAttemptAt is { } attempt && attempt > lastSuccess)
        {
            return nowUtc - attempt >= RetryAfterFailure;
        }
        return true;
    }
}
