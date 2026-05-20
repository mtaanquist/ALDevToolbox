using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Hosted service that drives the daily scheduled backup. Polls
/// <c>system_settings</c> once a minute and, if the configured time has
/// arrived since the last successful scheduled backup, kicks off a new one
/// in the background. Operators can disable the schedule without losing
/// the time-of-day setting.
///
/// <para>
/// The poll interval is intentionally short and tracked against
/// <see cref="Backup.CreatedAt"/> — the host clock isn't always in sync
/// with a wall clock at sub-minute precision, and we'd rather take the
/// backup a minute late than skip a day entirely.
/// </para>
/// </summary>
public sealed class BackupScheduler : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<BackupScheduler> _logger;

    public BackupScheduler(IServiceProvider services, TimeProvider clock, ILogger<BackupScheduler> logger)
    {
        _services = services;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Burn a few seconds before the first poll so startup migrations and
        // seed have finished — both touch the same connection pool we're
        // about to read from.
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BackupScheduler tick threw; will retry on the next poll.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var backups = scope.ServiceProvider.GetRequiredService<BackupService>();
        var perTenant = scope.ServiceProvider.GetRequiredService<PerTenantBackupService>();
        var offsite = scope.ServiceProvider.GetRequiredService<OffsiteBackupService>();
        await TickOnceAsync(db, backups, perTenant, offsite, ct);
    }

    /// <summary>
    /// One scheduler decision against the supplied scope. Public for tests
    /// (Issue #71): they drive the schedule decision matrix directly with a
    /// <see cref="TimeProvider"/> they control, bypassing the
    /// <c>Task.Delay</c> loop in <see cref="ExecuteAsync"/>.
    /// </summary>
    internal async Task TickOnceAsync(AppDbContext db, BackupService backups, PerTenantBackupService perTenant, OffsiteBackupService offsite, CancellationToken ct)
    {
        var settings = await db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (settings is null || !settings.BackupScheduleEnabled) return;

        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        var scheduled = settings.BackupScheduleTimeUtc;
        var todayWindow = new DateTime(
            nowUtc.Year, nowUtc.Month, nowUtc.Day,
            scheduled.Hour, scheduled.Minute, 0,
            DateTimeKind.Utc);

        // Run if we're at or past today's window and the most recent
        // scheduled backup is older than the window — i.e. today's slot
        // hasn't been served yet. Using `>=` rather than equality means
        // a poll that lands one minute late still triggers.
        if (nowUtc < todayWindow) return;

        var lastScheduled = await db.Backups.AsNoTracking()
            .Where(b => b.Kind == BackupKind.Scheduled)
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => (DateTime?)b.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (lastScheduled is not null && lastScheduled >= todayWindow) return;

        _logger.LogInformation(
            "BackupScheduler triggering scheduled backup (scheduled-for={Scheduled:o}, now={Now:o}).",
            todayWindow, nowUtc);
        Backup? created = null;
        try
        {
            created = await backups.CreateAsync(BackupKind.Scheduled, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled backup failed.");
        }

        if (created is not null)
        {
            try
            {
                await offsite.UploadAsync(created.Id, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Off-site upload failed for backup {FileName}.", created.FileName);
            }
            try
            {
                await offsite.PruneAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Off-site prune failed.");
            }
        }

        // Per-tenant snapshots run after the full backup so the full pg_dump
        // still exists if a per-tenant write fails midway. Each org is
        // independent — a failure on one org logs and continues.
        var orgs = await db.Organizations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(o => !o.IsSystem && !o.IsPending)
            .Select(o => o.Id)
            .ToListAsync(ct);
        foreach (var orgId in orgs)
        {
            PerTenantBackup? perTenantRow = null;
            try
            {
                var lastPerTenant = await db.PerTenantBackups
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(b => b.OrganizationId == orgId && b.Kind == BackupKind.Scheduled)
                    .OrderByDescending(b => b.CreatedAt)
                    .Select(b => (DateTime?)b.CreatedAt)
                    .FirstOrDefaultAsync(ct);
                if (lastPerTenant is not null && lastPerTenant >= todayWindow) continue;
                perTenantRow = await perTenant.CreateAsync(orgId, BackupKind.Scheduled, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled per-tenant snapshot failed for org {OrgId}.", orgId);
            }

            // Mirror the freshly-written per-tenant snapshot off-site too —
            // a tenant rolling back to yesterday is still a useful surface
            // after a whole-deployment disaster recovery.
            if (perTenantRow is not null)
            {
                try
                {
                    await offsite.UploadPerTenantAsync(perTenantRow.Id, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Off-site upload of per-tenant snapshot {FileName} (org {OrgId}) failed.",
                        perTenantRow.FileName, orgId);
                }
            }
        }
    }
}
