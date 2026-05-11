using ALDevToolbox.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ALDevToolbox.Services;

/// <summary>
/// Readiness probe: confirms the database connection is reachable and the EF
/// context can issue a query. Used by the <c>/health/ready</c> endpoint, which
/// load balancers and the Docker HEALTHCHECK should poll before sending traffic.
/// </summary>
public class DatabaseHealthCheck : IHealthCheck
{
    private readonly AppDbContext _db;

    public DatabaseHealthCheck(AppDbContext db)
    {
        _db = db;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var canConnect = await _db.Database.CanConnectAsync(cancellationToken);
            return canConnect
                ? HealthCheckResult.Healthy("Database reachable.")
                : HealthCheckResult.Unhealthy("Database is not reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database health check threw.", ex);
        }
    }
}
