using ALDevToolbox.Services.ObjectExplorer.Import;
namespace ALDevToolbox.Services;

/// <summary>
/// Flows an organisation identity through a non-HTTP async call chain (the
/// release-import background worker) so the same EF query filters and
/// <see cref="ReleaseImportService"/> org guard that protect a normal request
/// keep working when there's no <c>HttpContext</c>.
///
/// <para>
/// The value is captured from the submitting user's own request at enqueue
/// time and re-applied by the worker while it processes <em>that user's</em>
/// import — it never lets one request act as another org. <see cref="HttpOrganizationContext"/>
/// consults <see cref="Current"/> only as a fallback, so a real request (which
/// always has claims) is unaffected. This is the deferred-work analogue of the
/// "bootstrap / migration" cross-org sites blessed in CLAUDE.md, not a way to
/// widen a request's reach.
/// </para>
/// </summary>
public static class AmbientOrganizationScope
{
    private static readonly AsyncLocal<OrganizationIdentity?> _current = new();

    /// <summary>The identity in force for the current async flow, or null on a normal request.</summary>
    public static OrganizationIdentity? Current => _current.Value;

    /// <summary>
    /// Captured organisation identity. Mirrors the fields of
    /// <see cref="IOrganizationContext"/> that background work needs.
    /// </summary>
    public sealed record OrganizationIdentity(
        int OrganizationId,
        int? UserId,
        bool IsSiteAdmin,
        bool IsSystemOrganization)
    {
        /// <summary>
        /// Captures the identity of the request in flight, for work that will be
        /// handed to a background worker. <paramref name="what"/> completes the
        /// message of the <see cref="InvalidOperationException"/> thrown when
        /// there's no organisation in scope (e.g. "queuing a release import").
        /// </summary>
        public static OrganizationIdentity FromContext(IOrganizationContext context, string what)
        {
            ArgumentNullException.ThrowIfNull(context);
            return new OrganizationIdentity(
                OrganizationId: context.CurrentOrganizationId
                    ?? throw new InvalidOperationException($"No organization in scope when {what}."),
                UserId: context.CurrentUserId,
                IsSiteAdmin: context.IsSiteAdmin,
                IsSystemOrganization: context.IsSystemOrganization);
        }

        /// <summary>
        /// Identity for background work that acts for a whole organisation rather
        /// than for one signed-in user — the scheduler sweeps. Pass the org row's
        /// real <c>IsSystem</c>: it decides whether storage-quota and template-import
        /// rules treat the org as the system org, so a scheduled action must see the
        /// same value the interactive path would.
        /// </summary>
        public static OrganizationIdentity ForOrganization(int organizationId, bool isSystem, int? userId = null) =>
            new(organizationId, userId, IsSiteAdmin: false, IsSystemOrganization: isSystem);
    }

    /// <summary>
    /// Installs <paramref name="identity"/> for the lifetime of the returned
    /// scope. Dispose (via <c>using</c>) to clear it; nested scopes restore the
    /// previous value so a worker loop can't leak one job's org into the next.
    /// </summary>
    public static IDisposable Enter(OrganizationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var previous = _current.Value;
        _current.Value = identity;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly OrganizationIdentity? _previous;
        private bool _disposed;
        public Scope(OrganizationIdentity? previous) => _previous = previous;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current.Value = _previous;
        }
    }
}
