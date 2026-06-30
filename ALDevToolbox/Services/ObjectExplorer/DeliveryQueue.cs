using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// In-process hand-off from a request (the "Release this build now" action) to
/// <see cref="DeliveryWorker"/>, which runs the BC publish off the request thread. A
/// small bounded <see cref="Channel{T}"/> — not an external broker — keeps the "no
/// external services" fence intact, mirroring <see cref="ProjectDiscoveryQueue"/> and
/// <see cref="ReleaseImportQueue"/>. The persisted <c>oe_project_deliveries</c> row is
/// the source of truth; this channel just carries the id + captured identity.
///
/// <para>
/// In this slice a delivery runs immediately on enqueue — there is no time-based
/// scheduler (that, plus the cancel/claim race and restart-resume of <em>queued</em>
/// rows, is a later slice). The in-memory dedupe keyed on delivery id stops a
/// double-click from enqueuing the same delivery twice.
/// </para>
/// </summary>
public sealed class DeliveryQueue
{
    private readonly Channel<DeliveryJob> _channel =
        Channel.CreateBounded<DeliveryJob>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private readonly ConcurrentDictionary<int, byte> _inFlight = new();

    public ChannelReader<DeliveryJob> Reader => _channel.Reader;

    /// <summary>
    /// Enqueues a delivery unless one for the same id is already queued or running.
    /// Returns <c>true</c> when enqueued, <c>false</c> when coalesced.
    /// </summary>
    public async ValueTask<bool> EnqueueAsync(DeliveryJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!_inFlight.TryAdd(job.DeliveryId, 0)) return false;
        try
        {
            await _channel.Writer.WriteAsync(job, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            _inFlight.TryRemove(job.DeliveryId, out _);
            throw;
        }
    }

    /// <summary>Clears the in-flight flag once the worker has finished a delivery (success or failure).</summary>
    public void Complete(int deliveryId) => _inFlight.TryRemove(deliveryId, out _);
}

/// <summary>
/// A queued delivery run, executed by <see cref="DeliveryWorker"/> under the
/// triggering user's captured identity so the EF query filter and credential
/// resolution behave exactly as in the original request.
/// </summary>
public sealed record DeliveryJob(int DeliveryId, AmbientOrganizationScope.OrganizationIdentity Identity);
