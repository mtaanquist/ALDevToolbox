using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Hosted service that imports new Microsoft OnPrem Business Central releases
/// once a day for every org that opted in
/// (<c>OrganizationSettings.AutoImportReleasesEnabled</c> + a country). Mirrors
/// <see cref="BackupScheduler"/>'s poll-every-minute / run-once-daily shape and
/// <c>PerTenantBackupService</c>'s per-org iteration.
///
/// <para>
/// Each org's import runs under its own <see cref="AmbientOrganizationScope"/>
/// so EF query filters and the importer's org guard behave exactly as in a real
/// request — no new <c>IgnoreQueryFilters()</c> inside the per-org work. The
/// sweep is naturally idempotent: <see cref="ArtifactReleaseImporter"/> skips a
/// version whose release label already exists, so a re-run (after a restart, or
/// the same day) downloads nothing new. The run hour is configurable via
/// <c>RELEASE_AUTO_IMPORT_HOUR_UTC</c>; opt out entirely with
/// <c>DISABLE_RELEASE_AUTO_IMPORT_SCHEDULER=1</c>.
/// </para>
/// </summary>
public sealed class ReleaseAutoImportScheduler : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<ReleaseAutoImportScheduler> _logger;
    private readonly WorkerHeartbeat _heartbeat;
    private readonly int _hourUtc;

    // In-memory "ran today" guard. Re-running is harmless (dedup), so we don't
    // persist this — a restart at worst re-sweeps once, which enqueues nothing
    // new because every existing release label is skipped.
    private DateOnly _lastSweepDate = DateOnly.MinValue;

    public ReleaseAutoImportScheduler(
        IServiceProvider services,
        TimeProvider clock,
        ILogger<ReleaseAutoImportScheduler> logger,
        WorkerHeartbeatRegistry heartbeats)
    {
        _services = services;
        _clock = clock;
        _logger = logger;
        _hourUtc = ResolveHourUtc();
        // Poll every minute; the sweep resolves + enqueues quickly (downloads run
        // on ReleaseImportWorker, not here), so a 30-minute active ceiling is
        // ample even for many opted-in orgs.
        _heartbeat = heartbeats.Register(nameof(ReleaseAutoImportScheduler),
            maxActiveDuration: TimeSpan.FromMinutes(30),
            maxIdleSilence: TimeSpan.FromMinutes(5));
    }

    private static int ResolveHourUtc()
    {
        var raw = Environment.GetEnvironmentVariable("RELEASE_AUTO_IMPORT_HOUR_UTC");
        if (int.TryParse(raw, out var hour) && hour is >= 0 and <= 23) return hour;
        return 4; // Quiet pre-dawn UTC window, after the 3am OE vacuum.
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
                _logger.LogError(ex, "ReleaseAutoImportScheduler tick threw; will retry on the next poll.");
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
    /// One pass over every opted-in org. Internal so a test can drive it directly
    /// against a seeded database without the <see cref="Task.Delay"/> loop.
    /// </summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        List<(int OrganizationId, string Country)> targets;
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Active orgs only (skip the system org + pending signups).
            var activeOrgIds = await db.Organizations.IgnoreQueryFilters().AsNoTracking()
                .Where(o => !o.IsSystem && !o.IsPending)
                .Select(o => o.Id)
                .ToListAsync(ct).ConfigureAwait(false);
            var activeSet = activeOrgIds.ToHashSet();

            var rows = await db.OrganizationSettings.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.AutoImportReleasesEnabled && s.AutoImportCountry != null && s.AutoImportCountry != "")
                .Select(s => new { s.OrganizationId, s.AutoImportCountry })
                .ToListAsync(ct).ConfigureAwait(false);
            targets = rows
                .Where(r => activeSet.Contains(r.OrganizationId))
                .Select(r => (r.OrganizationId, r.AutoImportCountry!))
                .ToList();
        }

        if (targets.Count == 0) return;
        _logger.LogInformation("ReleaseAutoImportScheduler sweeping {Count} opted-in org(s).", targets.Count);

        foreach (var (orgId, country) in targets)
        {
            try
            {
                using var ambient = AmbientOrganizationScope.Enter(
                    new AmbientOrganizationScope.OrganizationIdentity(orgId, null, false, false));
                await using var scope = _services.CreateAsyncScope();
                var importer = scope.ServiceProvider.GetRequiredService<ArtifactReleaseImporter>();
                var outcome = await importer.ImportAsync(country, version: null, ct).ConfigureAwait(false);
                if (outcome.Status == ArtifactImportStatus.Queued)
                {
                    _logger.LogInformation(
                        "Auto-import queued {Label} for org {OrgId}.", outcome.Label, orgId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-import failed for org {OrgId} (country {Country}).", orgId, country);
            }
        }
    }
}
