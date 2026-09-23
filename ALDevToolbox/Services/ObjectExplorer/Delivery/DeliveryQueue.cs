using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.ObjectExplorer.Delivery;

/// <summary>
/// In-process hand-off from a request (the "Deploy now" action) to
/// <see cref="DeliveryWorker"/>, which runs the BC publish off the request thread. A
/// small bounded <see cref="System.Threading.Channels.Channel{T}"/> — not an external broker — keeps the "no
/// external services" fence intact, mirroring <see cref="ProjectDiscoveryQueue"/> and
/// <see cref="ReleaseImportQueue"/>. The persisted <c>oe_project_deliveries</c> row is
/// the source of truth; this channel just carries the id + captured identity.
///
/// <para>
/// <see cref="DeliveryScheduler"/> enqueues a delivery when its scheduled time comes
/// (a deploy-now is simply due at once), and on its first sweep fails the rows a
/// restart interrupted. The in-memory dedupe keyed on delivery id stops the scheduler
/// and a double-click from enqueuing the same delivery twice.
/// </para>
/// </summary>
public sealed class DeliveryQueue : JobQueue<DeliveryJob, int>
{
    public DeliveryQueue() : base(capacity: 64, keySelector: job => job.DeliveryId) { }
}

/// <summary>
/// A queued delivery run, executed by <see cref="DeliveryWorker"/> under the
/// triggering user's captured identity so the EF query filter and credential
/// resolution behave exactly as in the original request.
/// </summary>
public sealed record DeliveryJob(int DeliveryId, AmbientOrganizationScope.OrganizationIdentity Identity);
