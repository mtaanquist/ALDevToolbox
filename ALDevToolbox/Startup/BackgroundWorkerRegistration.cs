using ALDevToolbox.Services;

namespace ALDevToolbox.Startup;

/// <summary>
/// The polled schedulers. Queue-backed workers are registered next to the
/// services they drain (see Services/Workers/ for the shared base classes).
/// </summary>
public static class BackgroundWorkerRegistration
{
    /// <summary>Registers the scheduled background work.</summary>
    public static IServiceCollection AddBackgroundWorkers(this IServiceCollection services, bool singleTenantMode)
    {
        // Every scheduler below honours its own DISABLE_* opt-out inside the service
        // (see Services/Workers/PolledScheduler.cs), so registration is unconditional
        // except where a second condition applies.
        services.AddHostedService<BackupScheduler>();
        // Daily VACUUM over the Object Explorer content tables.
        services.AddHostedService<ALDevToolbox.Services.ObjectExplorer.ObjectExplorerVacuumScheduler>();
        // Refreshes per-org storage snapshots so StorageBar reads a cached row rather
        // than counting every tenanted table on each navigation.
        // Single-tenant mode hides the StorageBar entirely, so there's nothing to feed —
        // skip the timer there too.
        if (!singleTenantMode)
        {
            services.AddHostedService<ALDevToolbox.Services.UsageSnapshotScheduler>();
        }
        // Daily import of new Microsoft OnPrem releases for orgs that opted in
        // (OrganizationSettings.AutoImportReleasesEnabled); runs in single- and
        // multi-tenant alike.
        services.AddHostedService<ALDevToolbox.Services.ObjectExplorer.ReleaseAutoImportScheduler>();
        // Enqueues scheduled SaaS deliveries when due, and fails restart-orphaned ones on its
        // first sweep.
        services.AddHostedService<ALDevToolbox.Services.ObjectExplorer.DeliveryScheduler>();
        // Nightly sweep that re-reads every BC-connected project's environments, keeping the
        // mirrored next-platform-update columns fresh for the fleet view.
        services.AddHostedService<ALDevToolbox.Services.ObjectExplorer.Bc.EnvironmentRefreshScheduler>();
        // Mirrors Microsoft's BCQuality knowledge base into Postgres for the MCP
        // tools: a first ingest shortly after startup, then daily. With the refresh
        // disabled the tools report an empty knowledge base rather than failing.
        // See .design/bcquality.md.
        services.AddHostedService<ALDevToolbox.Services.BcQuality.BcQualityRefreshScheduler>();
        // Periodic prune of old login_attempts rows so the table doesn't grow
        // unbounded (the rate-limiter only reads a ~15-minute window). See issue #403.
        services.AddHostedService<ALDevToolbox.Services.LoginAttemptPruneScheduler>();
        return services;
    }
}
