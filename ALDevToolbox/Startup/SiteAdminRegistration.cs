using ALDevToolbox.Services;

namespace ALDevToolbox.Startup;

/// <summary>
/// The cross-org SiteAdmin console: system settings, backups, off-site storage
/// and storage usage.
/// </summary>
public static class SiteAdminRegistration
{
    /// <summary>Registers the SiteAdmin services, backups and the off-site restore worker.</summary>
    public static IServiceCollection AddSiteAdmin(this IServiceCollection services)
    {
        services.AddScoped<SystemSettingsService>();
        services.AddScoped<SiteAdminService>();
        services.AddScoped<BackupService>();
        services.AddScoped<PerTenantBackupService>();
        // Off-site storage backend (S3-compatible or Azure Blob) is chosen per request
        // from the decrypted settings; the factory is stateless, the providers it
        // returns are short-lived and built per call, so only the factory is registered.
        services.AddSingleton<ALDevToolbox.Services.Offsite.IOffsiteStorageProviderFactory,
            ALDevToolbox.Services.Offsite.OffsiteStorageProviderFactory>();
        services.AddScoped<OffsiteBackupService>();
        // Off-site restore download jobs: a singleton job tracker shared between
        // the SiteAdmin endpoint that enqueues and the worker that drains, and a
        // sequential BackgroundService that processes one download at a time.
        // Sequential because the bottleneck is the local disk on the
        // `app-backups` volume, not S3 throughput.
        services.AddSingleton<OffsiteRestoreJobs>();
        services.AddHostedService<OffsiteRestoreWorker>();
        services.AddScoped<DatabaseUsageService>();
        services.AddScoped<StorageQuotaGuard>();
        return services;
    }
}
