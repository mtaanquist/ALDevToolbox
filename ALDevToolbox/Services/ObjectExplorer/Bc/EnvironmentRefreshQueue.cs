using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// In-process hand-off of "re-read this project's Business Central environments and
/// re-mirror their next platform update" to <see cref="EnvironmentRefreshWorker"/>. Fed
/// by the nightly <see cref="EnvironmentRefreshScheduler"/> sweep and by an on-demand
/// refresh from a page. A small bounded <see cref="Channel{T}"/> — not an external queue
/// — keeps the "no external services" fence intact, mirroring
/// <see cref="ProjectDiscoveryQueue"/>.
///
/// <para>
/// In-flight state is tracked <em>in memory only</em> (project ids), never persisted: a
/// refresh is a disposable cache warm, so a restart drops any queued entry rather than
/// stranding a flag, and the next nightly sweep picks the project up again. The flag also
/// dedupes — a sweep enqueuing a project someone just refreshed by hand coalesces into
/// the one already running rather than making the same round trips twice.
/// </para>
/// See <c>.design/saas-delivery.md</c> and issue #657.
/// </summary>
public sealed class EnvironmentRefreshQueue
{
    // A nightly sweep offers every BC-connected project at once, so the bound is set for
    // a fleet rather than for a single page action; the worker drains one at a time and a
    // full channel simply makes the scheduler wait.
    private readonly Channel<EnvironmentRefreshJob> _channel =
        Channel.CreateBounded<EnvironmentRefreshJob>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private readonly ConcurrentDictionary<int, byte> _inFlight = new();

    public ChannelReader<EnvironmentRefreshJob> Reader => _channel.Reader;

    /// <summary>
    /// Enqueues a refresh for <paramref name="job"/>'s project unless one is already
    /// queued or running for it (the dedupe gate). Returns <c>true</c> when it was
    /// enqueued, <c>false</c> when coalesced into an in-flight refresh.
    /// </summary>
    public async ValueTask<bool> EnqueueAsync(EnvironmentRefreshJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        // Claim the in-flight slot first so a concurrent enqueue for the same project
        // loses the race and coalesces. Released by Complete() (worker) or here if the
        // write itself fails.
        if (!_inFlight.TryAdd(job.ProjectId, 0)) return false;
        try
        {
            await _channel.Writer.WriteAsync(job, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            _inFlight.TryRemove(job.ProjectId, out _);
            throw;
        }
    }

    /// <summary>True while a refresh for <paramref name="projectId"/> is queued or running.</summary>
    public bool IsInFlight(int projectId) => _inFlight.ContainsKey(projectId);

    /// <summary>Clears the in-flight flag once the worker has finished a project (success or failure).</summary>
    public void Complete(int projectId) => _inFlight.TryRemove(projectId, out _);
}

/// <summary>
/// A queued environment refresh for one project, run by
/// <see cref="EnvironmentRefreshWorker"/> under the captured
/// <see cref="AmbientOrganizationScope.OrganizationIdentity">identity</see> — required so
/// the EF query filter scopes the work to the project's own organisation off-request.
/// </summary>
public sealed record EnvironmentRefreshJob(int ProjectId, AmbientOrganizationScope.OrganizationIdentity Identity);
