using ALDevToolbox.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Drains <see cref="DeliveryQueue"/> and runs each BC publish off the request
/// thread. Mirrors <see cref="ProjectDiscoveryWorker"/>: one delivery at a time
/// (single-reader channel), each in its own DI scope under the triggering user's
/// <see cref="AmbientOrganizationScope"/> identity so the org query filter and
/// credential resolution behave as they did in the request. <see cref="DeliveryService"/>
/// captures every failure onto the persisted delivery row, so this loop's own
/// try/catch is only the last-resort net that keeps one bad delivery from killing the
/// worker. See <c>.design/saas-delivery.md</c> ("Services &amp; seams").
/// </summary>
public sealed class DeliveryWorker : BackgroundService
{
    private readonly DeliveryQueue _queue;
    private readonly IServiceProvider _services;
    private readonly ILogger<DeliveryWorker> _logger;
    private readonly WorkerHeartbeat _heartbeat;

    public DeliveryWorker(
        DeliveryQueue queue,
        IServiceProvider services,
        ILogger<DeliveryWorker> logger,
        WorkerHeartbeatRegistry heartbeats)
    {
        _queue = queue;
        _services = services;
        _logger = logger;
        // Queue-driven: idle until a release is triggered (no idle-silence ceiling). A
        // single publish uploads a handful of apps and polls install status; 30 minutes
        // is generous headroom while still catching a wedged run.
        _heartbeat = heartbeats.Register(nameof(DeliveryWorker),
            maxActiveDuration: TimeSpan.FromMinutes(30),
            maxIdleSilence: null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            _heartbeat.BeginActive();
            try
            {
                await RunJobAsync(job, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delivery worker tripped on DeliveryId={DeliveryId}.", job.DeliveryId);
            }
            finally
            {
                _queue.Complete(job.DeliveryId);
                _heartbeat.EndActive();
            }
        }
    }

    private async Task RunJobAsync(DeliveryJob job, CancellationToken ct)
    {
        using var orgScope = AmbientOrganizationScope.Enter(job.Identity);
        await using var scope = _services.CreateAsyncScope();
        var deliveries = scope.ServiceProvider.GetRequiredService<DeliveryService>();
        await deliveries.RunDeliveryAsync(job.DeliveryId, ct).ConfigureAwait(false);
    }
}
