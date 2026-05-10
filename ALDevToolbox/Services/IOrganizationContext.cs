namespace ALDevToolbox.Services;

/// <summary>
/// Per-request resolver for the acting user's organisation. Backed by the
/// <c>organization_id</c> claim on the auth cookie at runtime; tests and
/// seed-time bootstrap install a static implementation.
///
/// EF query filters on <c>AppDbContext</c> read <see cref="CurrentOrganizationId"/>
/// so every read scopes to a single organisation. When the value is
/// <see langword="null"/>, the filter never matches — pre-login flows that need
/// cross-org reads (login, bootstrap, signup) call <c>IgnoreQueryFilters()</c>
/// explicitly.
/// </summary>
public interface IOrganizationContext
{
    /// <summary>The acting user's organisation, or <c>null</c> when no user is signed in.</summary>
    int? CurrentOrganizationId { get; }

    /// <summary>The acting user's id, or <c>null</c> when no user is signed in.</summary>
    int? CurrentUserId { get; }

    /// <summary>
    /// Sentinel for the EF query filter — returns the current org id, or
    /// <c>0</c> when no user is signed in. Real organisation ids start at 1
    /// so filtering by this value matches nothing pre-login.
    /// </summary>
    int OrganizationIdForFilter { get; }
}
