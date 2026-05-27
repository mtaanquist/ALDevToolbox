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
        bool IsSystemOrganization);

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
