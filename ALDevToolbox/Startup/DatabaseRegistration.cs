using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ALDevToolbox.Startup;

/// <summary>The EF Core context and its audit interceptor.</summary>
public static class DatabaseRegistration
{
    /// <summary>Registers <see cref="AppDbContext"/> against the configured Postgres.</summary>
    public static IServiceCollection AddAppDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        // Postgres connection string (M16). `ConnectionStrings__DefaultConnection`
        // is the deployment knob; compose.yml builds it from POSTGRES_* env vars and
        // passes it through. There is no fallback DSN — failing fast surfaces a
        // missing config sooner than discovering it at first query.
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is not configured. Set ConnectionStrings__DefaultConnection.");
        services.AddScoped<AuditInterceptor>();
        // The model snapshot stays a hand-rolled affair (the InitialCreate designer
        // file's BuildTargetModel is intentionally empty), so EF's pending-model-
        // changes guard would fire on every MigrateAsync. Real schema drift still
        // surfaces when the migration itself runs.
        // No MaxBatchSize override: the Npgsql default (1000) applies. The old cap of
        // 100 existed because oe_module_files carried the source text inline (50 KB a
        // row), so a full batch built a multi-megabyte command. That blob moved to the
        // content-addressed oe_file_contents table, which
        // OeIngestHelpers.UpsertFileContentsAsync writes with a single raw unnest
        // INSERT that never enters an EF batch. What is left on the EF ingest path is
        // narrow, high-volume rows (oe_module_references / _symbols / _variables /
        // _objects), where the cap only multiplied the round-trips by ten. See #688.
        services.AddDbContext<AppDbContext>((sp, options) =>
            options
                .UseNpgsql(connectionString, npgsql => npgsql
                    .UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));
        return services;
    }
}
