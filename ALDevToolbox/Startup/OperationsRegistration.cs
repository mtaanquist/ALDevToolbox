using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using System.Threading.RateLimiting;

namespace ALDevToolbox.Startup;

/// <summary>
/// Operator-facing plumbing: the health checks behind /healthz, /readyz and
/// /healthz/workers, plus the anonymous-write rate limiter.
/// </summary>
public static class OperationsRegistration
{
    /// <summary>Registers the health checks and the DCR rate-limiter policy.</summary>
    public static IServiceCollection AddOperations(this IServiceCollection services)
    {
        // Health checks (M21). /healthz is the live probe — green when the database
        // is reachable and the Data Protection key ring round-trips. /readyz is
        // distinct: it stays red until startup work (migrations + seed) has finished,
        // so reverse proxies don't send traffic mid-migration.
        services.AddSingleton<StartupReadinessState>();
        services.AddScoped<DatabaseHealthCheck>();
        services.AddSingleton<DataProtectionHealthCheck>();
        services.AddSingleton<StartupReadinessHealthCheck>();
        // Singleton registry shared by every BackgroundService — each worker registers
        // its own WorkerHeartbeat at construction and beats while running. The check
        // reads them out-of-band and is surfaced on its own /healthz/workers endpoint
        // (tag "workers"), NOT on /healthz: the Dockerfile HEALTHCHECK polls /healthz
        // and would otherwise kill an otherwise-serving container just because a
        // background job is slow, contradicting the documented liveness contract. See
        // issue #377.
        services.AddSingleton<WorkerHeartbeatRegistry>();
        services.AddSingleton<BackgroundWorkerHealthCheck>();
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: new[] { "healthz" })
            .AddCheck<DataProtectionHealthCheck>("data-protection", tags: new[] { "healthz" })
            .AddCheck<BackgroundWorkerHealthCheck>("background-workers", tags: new[] { "workers" })
            .AddCheck<StartupReadinessHealthCheck>("startup", tags: new[] { "readyz" });

        // Rate limiter for the anonymous, unbounded-write surfaces. /oauth/register
        // (RFC 7591 Dynamic Client Registration) is AllowAnonymous and creates an
        // oauth_applications row per POST, so a trivial script could grow the table
        // without limit. A per-IP fixed window caps the drip. See issue #378.
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(OAuthEndpoints.DcrRateLimitPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(10),
                        QueueLimit = 0,
                    }));
        });
        return services;
    }
}
