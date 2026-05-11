using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ALDevToolbox.Services;

/// <summary>
/// Reports whether startup work (EF migrations, first-run seed, bootstrap
/// admin) has finished. Drives the <c>/readyz</c> endpoint so reverse proxies
/// only route traffic to a fully-initialised container.
/// </summary>
public sealed class StartupReadinessHealthCheck : IHealthCheck
{
    private readonly StartupReadinessState _state;

    public StartupReadinessHealthCheck(StartupReadinessState state)
    {
        _state = state;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_state.IsReady
            ? HealthCheckResult.Healthy("Startup work complete.")
            : HealthCheckResult.Unhealthy("Startup work has not finished."));
    }
}
