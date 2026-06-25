using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Hosted service that rebuilds opted-in customers (<c>Customer.AutoBuildEnabled</c>)
/// once a day whenever their source has moved. Mirrors
/// <see cref="ReleaseAutoImportScheduler"/>'s poll-every-minute / run-once-daily
/// shape and per-org iteration.
///
/// <para>
/// For each enabled customer the sweep probes every repo's remote HEAD with
/// <c>git ls-remote</c> (no clone) via
/// <see cref="CustomerBuildService.HasRepoChangesSinceLastBuildAsync"/>; only when
/// a HEAD differs from the last build's recorded commit does it queue a new build
/// (<see cref="CustomerReleaseImporter.StartBuildAsync"/>) — a new release per
/// change, leaving the unchanged customers untouched. The <c>commit_sha</c>
/// provenance captured per build is the dedup key, so re-running the sweep (after a
/// restart, or the same day) builds nothing new. Each org's work runs under its own
/// <see cref="AmbientOrganizationScope"/> so EF query filters and the importer's org
/// guard behave exactly as in a real request — no new <c>IgnoreQueryFilters()</c>
/// inside the per-org work. The run hour is configurable via
/// <c>CUSTOMER_AUTO_BUILD_HOUR_UTC</c>; opt out entirely with
/// <c>DISABLE_CUSTOMER_AUTO_BUILD_SCHEDULER=1</c>. See
/// <c>.design/object-explorer-customer-builds.md</c> ("Auto-build").
/// </para>
/// </summary>
public sealed class CustomerAutoBuildScheduler : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<CustomerAutoBuildScheduler> _logger;
    private readonly WorkerHeartbeat _heartbeat;
    private readonly int _hourUtc;

    // In-memory "ran today" guard. Re-running is harmless (the commit_sha dedup
    // skips unchanged customers), so we don't persist it — a restart at worst
    // re-sweeps once and queues nothing new.
    private DateOnly _lastSweepDate = DateOnly.MinValue;

    public CustomerAutoBuildScheduler(
        IServiceProvider services,
        TimeProvider clock,
        ILogger<CustomerAutoBuildScheduler> logger,
        WorkerHeartbeatRegistry heartbeats)
    {
        _services = services;
        _clock = clock;
        _logger = logger;
        _hourUtc = ResolveHourUtc();
        // The sweep does a handful of ls-remote probes per customer then enqueues;
        // the heavy clone/compile runs on ReleaseImportWorker, not here.
        _heartbeat = heartbeats.Register(nameof(CustomerAutoBuildScheduler),
            maxActiveDuration: TimeSpan.FromMinutes(30),
            maxIdleSilence: TimeSpan.FromMinutes(5));
    }

    private static int ResolveHourUtc()
    {
        var raw = Environment.GetEnvironmentVariable("CUSTOMER_AUTO_BUILD_HOUR_UTC");
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
                _logger.LogError(ex, "CustomerAutoBuildScheduler tick threw; will retry on the next poll.");
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
    /// One pass over every opted-in customer. Internal so a test can drive it
    /// directly against a seeded database without the <see cref="Task.Delay"/> loop.
    /// </summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        List<(int OrganizationId, int CustomerId, string Name)> targets;
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Active orgs only (skip the system org + pending signups).
            var activeOrgIds = (await db.Organizations.IgnoreQueryFilters().AsNoTracking()
                .Where(o => !o.IsSystem && !o.IsPending)
                .Select(o => o.Id)
                .ToListAsync(ct).ConfigureAwait(false)).ToHashSet();

            var rows = await db.OeCustomers.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.AutoBuildEnabled && c.DeletedAt == null && c.Repositories.Count > 0)
                .Select(c => new { c.OrganizationId, c.Id, c.Name })
                .ToListAsync(ct).ConfigureAwait(false);
            targets = rows
                .Where(r => activeOrgIds.Contains(r.OrganizationId))
                .Select(r => (r.OrganizationId, r.Id, r.Name))
                .ToList();
        }

        if (targets.Count == 0) return;
        _logger.LogInformation("CustomerAutoBuildScheduler checking {Count} opted-in customer(s).", targets.Count);

        foreach (var (orgId, customerId, name) in targets)
        {
            try
            {
                using var ambient = AmbientOrganizationScope.Enter(
                    new AmbientOrganizationScope.OrganizationIdentity(orgId, null, false, false));
                await using var scope = _services.CreateAsyncScope();
                var builds = scope.ServiceProvider.GetRequiredService<CustomerBuildService>();
                if (!await builds.HasRepoChangesSinceLastBuildAsync(customerId, ct).ConfigureAwait(false))
                {
                    continue; // source hasn't moved since the last build
                }

                var importer = scope.ServiceProvider.GetRequiredService<CustomerReleaseImporter>();
                var releaseId = await importer.StartBuildAsync(customerId, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "Auto-build queued for customer {Customer} (release {ReleaseId}, org {OrgId}).",
                    name, releaseId, orgId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-build failed for customer {CustomerId} (org {OrgId}).", customerId, orgId);
            }
        }
    }
}
