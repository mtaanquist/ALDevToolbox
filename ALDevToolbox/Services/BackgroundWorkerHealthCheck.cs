using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ALDevToolbox.Services;

/// <summary>
/// Liveness probe for every <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>
/// that registers a <see cref="WorkerHeartbeat"/>. Tagged <c>healthz</c> so it
/// joins the existing live probe — a stuck DVD import or a silent scheduler
/// loop now flips <c>/healthz</c> to 503, which the Docker <c>HEALTHCHECK</c>
/// (and any reverse proxy / orchestrator polling it) acts on.
///
/// <para>Each registered worker carries its own thresholds (<see cref="WorkerHeartbeat.MaxActiveDuration"/>,
/// <see cref="WorkerHeartbeat.MaxIdleSilence"/>); this check just evaluates them
/// against the wall clock at call time. The unhealthy message names every
/// failing worker plus its reason so an operator reading <c>/healthz</c>'s
/// detail can tell <i>which</i> background task is stuck without digging into
/// logs.</para>
/// </summary>
public sealed class BackgroundWorkerHealthCheck : IHealthCheck
{
    private readonly WorkerHeartbeatRegistry _registry;
    private readonly TimeProvider _clock;

    public BackgroundWorkerHealthCheck(WorkerHeartbeatRegistry registry, TimeProvider clock)
    {
        _registry = registry;
        _clock = clock;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var workers = _registry.All();
        if (workers.Count == 0)
        {
            // No workers have registered yet — most likely the host is still
            // booting. Don't fail health on that; StartupReadinessHealthCheck
            // owns the boot-window signal.
            return Task.FromResult(HealthCheckResult.Healthy("No background workers registered."));
        }

        var failures = new List<string>();
        foreach (var hb in workers)
        {
            if (!hb.IsHealthy(now, out var reason))
            {
                failures.Add(reason!);
            }
        }

        if (failures.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                $"All {workers.Count} background worker(s) responsive."));
        }
        return Task.FromResult(HealthCheckResult.Unhealthy(string.Join(" | ", failures)));
    }
}
