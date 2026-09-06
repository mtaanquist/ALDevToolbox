using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// In-process hand-off of "re-read this project's Business Central environments and
/// re-mirror their next platform update" to <see cref="EnvironmentRefreshWorker"/>. Fed
/// by the nightly <see cref="EnvironmentRefreshScheduler"/> sweep and by an on-demand
/// refresh from a page. A small bounded <see cref="System.Threading.Channels.Channel{T}"/> — not an external queue
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
public sealed class EnvironmentRefreshQueue : JobQueue<EnvironmentRefreshJob, int>
{
    // A nightly sweep offers every BC-connected project at once, so the bound is set for
    // a fleet rather than for a single page action; the worker drains one at a time and a
    // full channel simply makes the scheduler wait.
    public EnvironmentRefreshQueue() : base(capacity: 256, keySelector: job => job.ProjectId) { }
}

/// <summary>
/// A queued environment refresh for one project, run by
/// <see cref="EnvironmentRefreshWorker"/> under the captured
/// <see cref="AmbientOrganizationScope.OrganizationIdentity">identity</see> — required so
/// the EF query filter scopes the work to the project's own organisation off-request.
/// </summary>
public sealed record EnvironmentRefreshJob(int ProjectId, AmbientOrganizationScope.OrganizationIdentity Identity);
