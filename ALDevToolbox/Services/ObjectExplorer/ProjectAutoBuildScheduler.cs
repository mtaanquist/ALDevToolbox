using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Hosted service that rebuilds opted-in projects (<c>Project.AutoBuildEnabled</c>)
/// once a day whenever their source has moved. Mirrors
/// <see cref="ReleaseAutoImportScheduler"/>'s poll-every-minute / run-once-daily
/// shape and per-org iteration.
///
/// <para>
/// For each enabled project the sweep probes every repo's remote HEAD with
/// <c>git ls-remote</c> (no clone) via
/// <see cref="ProjectBuildService.HasRepoChangesSinceLastBuildAsync"/>; only when
/// a HEAD differs from the last build's recorded commit does it queue a new build
/// (<see cref="ProjectBuildImporter.StartBuildAsync"/>) — a new release per
/// change, leaving the unchanged projects untouched. The <c>commit_sha</c>
/// provenance captured per build is the dedup key, so re-running the sweep (after a
/// restart, or the same day) builds nothing new. Each org's work runs under its own
/// <see cref="AmbientOrganizationScope"/> so EF query filters and the importer's org
/// guard behave exactly as in a real request — no new <c>IgnoreQueryFilters()</c>
/// inside the per-org work. The run hour is configurable via
/// <c>PROJECT_AUTO_BUILD_HOUR_UTC</c>; opt out entirely with
/// <c>DISABLE_PROJECT_AUTO_BUILD_SCHEDULER=1</c>. See
/// <c>.design/object-explorer-project-builds.md</c> ("Auto-build").
/// </para>
/// </summary>
public sealed class ProjectAutoBuildScheduler : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProjectAutoBuildScheduler> _logger;
    private readonly WorkerHeartbeat _heartbeat;
    private readonly int _hourUtc;

    // In-memory "ran today" guard. Re-running is harmless (the commit_sha dedup
    // skips unchanged projects), so we don't persist it — a restart at worst
    // re-sweeps once and queues nothing new.
    private DateOnly _lastSweepDate = DateOnly.MinValue;

    public ProjectAutoBuildScheduler(
        IServiceProvider services,
        TimeProvider clock,
        ILogger<ProjectAutoBuildScheduler> logger,
        WorkerHeartbeatRegistry heartbeats)
    {
        _services = services;
        _clock = clock;
        _logger = logger;
        _hourUtc = ResolveHourUtc();
        // The sweep does a handful of ls-remote probes per project then enqueues;
        // the heavy clone/compile runs on ReleaseImportWorker, not here.
        _heartbeat = heartbeats.Register(nameof(ProjectAutoBuildScheduler),
            maxActiveDuration: TimeSpan.FromMinutes(30),
            maxIdleSilence: TimeSpan.FromMinutes(5));
    }

    private static int ResolveHourUtc()
    {
        var raw = Environment.GetEnvironmentVariable("PROJECT_AUTO_BUILD_HOUR_UTC");
        if (int.TryParse(raw, out var hour) && hour is >= 0 and <= 23) return hour;
        return 5; // After the 4am artifact auto-import, so a parent release it needs is likelier present.
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup migrations + seed finish before the first poll.
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
                _logger.LogError(ex, "ProjectAutoBuildScheduler tick threw; will retry on the next poll.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        if (nowUtc.Hour < _hourUtc) return;
        var today = DateOnly.FromDateTime(nowUtc);
        if (_lastSweepDate >= today) return;

        await SweepAsync(ct).ConfigureAwait(false);
        _lastSweepDate = today;
    }

    /// <summary>
    /// One pass over every opted-in project. Internal so a test can drive it
    /// directly against a seeded database without the <see cref="Task.Delay"/> loop.
    /// </summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        List<(int OrganizationId, int ProjectId, string Name)> targets;
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Active orgs only (skip the system org + pending signups).
            var activeOrgIds = (await db.Organizations.IgnoreQueryFilters().AsNoTracking()
                .Where(o => !o.IsSystem && !o.IsPending)
                .Select(o => o.Id)
                .ToListAsync(ct).ConfigureAwait(false)).ToHashSet();

            var rows = await db.OeProjects.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.AutoBuildEnabled && c.DeletedAt == null && c.Repositories.Count > 0)
                .Select(c => new { c.OrganizationId, c.Id, c.Name })
                .ToListAsync(ct).ConfigureAwait(false);
            targets = rows
                .Where(r => activeOrgIds.Contains(r.OrganizationId))
                .Select(r => (r.OrganizationId, r.Id, r.Name))
                .ToList();
        }

        if (targets.Count == 0) return;
        _logger.LogInformation("ProjectAutoBuildScheduler checking {Count} opted-in project(s).", targets.Count);

        foreach (var (orgId, projectId, name) in targets)
        {
            try
            {
                using var ambient = AmbientOrganizationScope.Enter(
                    new AmbientOrganizationScope.OrganizationIdentity(orgId, null, false, false));
                await using var scope = _services.CreateAsyncScope();
                var builds = scope.ServiceProvider.GetRequiredService<ProjectBuildService>();
                // In-flight guard: a build queued (e.g. a manual one at 04:59) but
                // not yet run by the single-reader worker has no result rows, so
                // the change check below would see "HEAD moved" and enqueue a
                // duplicate release for the same commit. Skip if one is pending.
                // See issue #428.
                if (await builds.HasPendingBuildAsync(projectId, ct).ConfigureAwait(false))
                {
                    _logger.LogInformation(
                        "Auto-build skipped for project {Project} (org {OrgId}) — a build is already pending.",
                        name, orgId);
                    continue;
                }
                if (!await builds.HasRepoChangesSinceLastBuildAsync(projectId, ct).ConfigureAwait(false))
                {
                    continue; // source hasn't moved since the last build
                }

                var importer = scope.ServiceProvider.GetRequiredService<ProjectBuildImporter>();
                var releaseId = await importer.StartBuildAsync(projectId, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "Auto-build queued for project {Project} (release {ReleaseId}, org {OrgId}).",
                    name, releaseId, orgId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-build failed for project {ProjectId} (org {OrgId}).", projectId, orgId);
            }
        }
    }
}
