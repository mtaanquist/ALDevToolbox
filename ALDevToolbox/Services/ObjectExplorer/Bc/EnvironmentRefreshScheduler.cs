using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// Hosted service that once a night offers every Business-Central-connected project to
/// <see cref="EnvironmentRefreshQueue"/>, so the fleet view of "when does each customer
/// get their next platform update?" is answered from cached rows instead of a hundred
/// live round trips. Mirrors <see cref="DeliveryScheduler"/>: poll on a short interval,
/// enumerate active orgs, and do the per-org work inside that org's
/// <see cref="AmbientOrganizationScope"/> so the EF query filter behaves exactly as in a
/// request. The <em>only</em> cross-org read is the active-org enumeration (the same
/// blessed <c>IgnoreQueryFilters()</c> the existing schedulers use); the project query
/// stays org-scoped.
///
/// <para>
/// It runs at <see cref="SweepHourUtc"/>, once per UTC day — deliberately a quiet hour,
/// because a sweep is two admin-center calls per environment against every customer's
/// tenant. Nothing here is fatal: an org or a project that fails is logged and the sweep
/// carries on, and a missed night costs only staleness, which the mirrored fetched-at
/// timestamps make visible. Opt out with <c>DISABLE_ENVIRONMENT_REFRESH_SCHEDULER=1</c>.
/// See <c>.design/saas-delivery.md</c> and issue #657.
/// </para>
/// </summary>
public sealed class EnvironmentRefreshScheduler : BackgroundService
{
    /// <summary>The UTC hour the nightly sweep runs in — quiet for European working hours.</summary>
    internal const int SweepHourUtc = 3;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceProvider _services;
    private readonly EnvironmentRefreshQueue _queue;
    private readonly TimeProvider _clock;
    private readonly ILogger<EnvironmentRefreshScheduler> _logger;
    private readonly WorkerHeartbeat _heartbeat;

    // The last UTC date a sweep ran, so the five-minute poll fires the sweep once per
    // night rather than twelve times inside the sweep hour.
    private DateOnly? _lastSweptUtcDate;

    public EnvironmentRefreshScheduler(
        IServiceProvider services,
        EnvironmentRefreshQueue queue,
        TimeProvider clock,
        ILogger<EnvironmentRefreshScheduler> logger,
        WorkerHeartbeatRegistry heartbeats)
    {
        _services = services;
        _queue = queue;
        _clock = clock;
        _logger = logger;
        // Polls every 5 minutes; a sweep only enumerates and enqueues (the round trips
        // run on EnvironmentRefreshWorker), so a 10-minute active ceiling is ample.
        _heartbeat = heartbeats.Register(nameof(EnvironmentRefreshScheduler),
            maxActiveDuration: TimeSpan.FromMinutes(10),
            maxIdleSilence: TimeSpan.FromMinutes(30));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Environment.GetEnvironmentVariable("DISABLE_ENVIRONMENT_REFRESH_SCHEDULER") == "1")
        {
            _logger.LogInformation("EnvironmentRefreshScheduler disabled via DISABLE_ENVIRONMENT_REFRESH_SCHEDULER=1.");
            return;
        }

        // Let startup migrations + seed finish before the first poll.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            _heartbeat.Tick();
            try
            {
                var nowUtc = _clock.GetUtcNow().UtcDateTime;
                var today = DateOnly.FromDateTime(nowUtc);
                if (nowUtc.Hour == SweepHourUtc && _lastSweptUtcDate != today)
                {
                    _heartbeat.BeginActive();
                    try
                    {
                        await SweepAsync(stoppingToken);
                        _lastSweptUtcDate = today;
                    }
                    finally { _heartbeat.EndActive(); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EnvironmentRefreshScheduler tick threw; will retry on the next poll.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// One pass over every active org, enqueuing each of its BC-connected projects.
    /// Internal so a test can drive it directly against a seeded database without the
    /// <see cref="Task.Delay"/> loop. Returns how many projects were enqueued.
    /// </summary>
    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        List<(int Id, bool IsSystem)> orgs;
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // The one sanctioned cross-org read: which orgs to sweep. Only pending
            // signups are skipped — like DeliveryScheduler, the system org is swept too,
            // since in single-tenant deployments it is the working org.
            var rows = await db.Organizations.IgnoreQueryFilters().AsNoTracking()
                .Where(o => !o.IsPending)
                .Select(o => new { o.Id, o.IsSystem })
                .ToListAsync(ct).ConfigureAwait(false);
            orgs = rows.Select(o => (o.Id, o.IsSystem)).ToList();
        }

        var enqueued = 0;
        foreach (var (orgId, isSystem) in orgs)
        {
            try
            {
                var identity = AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem);
                using var ambient = AmbientOrganizationScope.Enter(identity);
                await using var scope = _services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var projectIds = await ResolveProjectIdsAsync(db, ct).ConfigureAwait(false);

                foreach (var projectId in projectIds)
                {
                    if (await _queue.EnqueueAsync(new EnvironmentRefreshJob(projectId, identity), ct).ConfigureAwait(false))
                    {
                        enqueued++;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EnvironmentRefreshScheduler sweep failed for org {OrgId}.", orgId);
            }
        }

        if (enqueued > 0)
        {
            _logger.LogInformation("EnvironmentRefreshScheduler enqueued {Count} project(s) for an environment refresh.", enqueued);
        }
        return enqueued;
    }

    /// <summary>
    /// The projects one org offers to a sweep: the live ones whose Business Central
    /// connection is complete enough to answer. A project missing any credential part
    /// would only produce a token failure per night, so it is not offered at all.
    /// Org-scoped by the EF query filter on <paramref name="db"/> — no
    /// <c>IgnoreQueryFilters()</c> here. Internal so a test can drive it against a seeded
    /// database without the hosted-service loop.
    /// </summary>
    internal static Task<List<int>> ResolveProjectIdsAsync(AppDbContext db, CancellationToken ct)
        => db.OeProjects.AsNoTracking()
            .Where(p => p.DeletedAt == null
                && p.BcTenantId != null
                && p.BcClientId != null
                && p.BcClientSecretEncrypted != null)
            .Select(p => p.Id)
            .ToListAsync(ct);
}
