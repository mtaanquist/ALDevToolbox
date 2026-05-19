namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Records that a <see cref="User"/> has granted an OAuth client permission to
/// act on their behalf for a specific organisation and scope set. The OAuth
/// consent screen looks here on every authorisation request: when an existing
/// row covers the requested scopes the screen auto-submits, so the user only
/// sees the consent UI on first connect (or when the requested scopes change).
///
/// Lives alongside the OpenIddict-managed tables (<c>oauth_applications</c>,
/// <c>oauth_authorizations</c>, <c>oauth_scopes</c>, <c>oauth_tokens</c>),
/// but is ours — OpenIddict's authorisations are short-lived per-grant
/// records and don't map cleanly to "the user has trusted this app".
/// </summary>
public class OAuthConsent
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>
    /// The OpenIddict client id this consent applies to. For DCR-registered
    /// clients this is the generated GUID; for CIMD it is the metadata
    /// document URL the client identified itself with.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Space-separated set of granted scopes, sorted alphabetically so a
    /// requested-scopes subset check is a string-prefix test rather than a
    /// set-difference scan.
    /// </summary>
    public string ScopesGranted { get; set; } = string.Empty;

    public DateTime GrantedAt { get; set; }

    /// <summary>Stamped when the user (or an admin) revokes the consent. Non-null rows are ignored.</summary>
    public DateTime? RevokedAt { get; set; }
}
