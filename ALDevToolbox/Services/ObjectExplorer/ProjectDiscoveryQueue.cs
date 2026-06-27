using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// In-process hand-off from a request (the pipeline editor's Refresh, or a
/// repo-change on a project) to <see cref="ProjectDiscoveryWorker"/>, which warms
/// the per-project discovered-extensions cache off the request thread. A small
/// bounded <see cref="Channel{T}"/> — not an external queue — keeps the "no
/// external services" fence intact, mirroring <see cref="ReleaseImportQueue"/>.
///
/// <para>
/// In-flight state is tracked <em>in memory only</em> (an
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> of project ids), never
/// persisted — a discovery is a disposable cache warm, so a restart simply drops
/// any queued/running entry rather than stranding a "discovering" flag. The flag
/// also dedupes: enqueuing a project that's already queued/running is a no-op, so
/// several pipeline editors refreshing the same project coalesce into one clone.
/// </para>
/// </summary>
public sealed class ProjectDiscoveryQueue
{
    // A modest bound: discovery is cheap (a blobless app.json clone) and the
    // worker processes one project at a time, so a deep backlog isn't expected.
    private readonly Channel<ProjectDiscoveryJob> _channel =
        Channel.CreateBounded<ProjectDiscoveryJob>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private readonly ConcurrentDictionary<int, byte> _inFlight = new();

    public ChannelReader<ProjectDiscoveryJob> Reader => _channel.Reader;

    /// <summary>
    /// Enqueues a discovery for <paramref name="job"/>'s project unless one is
    /// already queued or running for it (the dedupe gate). Returns <c>true</c> when
    /// it was enqueued, <c>false</c> when coalesced into an in-flight discovery.
    /// </summary>
    public async ValueTask<bool> EnqueueAsync(ProjectDiscoveryJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        // Claim the in-flight slot first so a concurrent enqueue for the same
        // project loses the race and coalesces. Released by Complete() (worker) or
        // here if the write itself fails.
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

    /// <summary>True while a discovery for <paramref name="projectId"/> is queued or running.</summary>
    public bool IsInFlight(int projectId) => _inFlight.ContainsKey(projectId);

    /// <summary>Clears the in-flight flag once the worker has finished a project (success or failure).</summary>
    public void Complete(int projectId) => _inFlight.TryRemove(projectId, out _);
}

/// <summary>
/// A queued cache-warming discovery for one project, run by
/// <see cref="ProjectDiscoveryWorker"/> under the requesting user's captured
/// <see cref="AmbientOrganizationScope.OrganizationIdentity">identity</see> — required
/// because <see cref="Account.UserRepositoryTokenService"/> resolves the repo PAT for
/// the acting user off-request.
/// </summary>
public sealed record ProjectDiscoveryJob(int ProjectId, AmbientOrganizationScope.OrganizationIdentity Identity);
