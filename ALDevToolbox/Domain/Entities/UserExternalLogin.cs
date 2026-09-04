namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A link between a local <see cref="User"/> and an identity at an external
/// provider. Two providers use this table, and they are not the same kind of
/// thing:
///
/// <list type="bullet">
/// <item><description><c>"entra"</c> — Microsoft Entra ID, the one federated
/// <em>sign-in</em>. These rows let someone log in.</description></item>
/// <item><description><c>"github"</c> — a GitHub account <em>link</em> (issue
/// #621). These rows never sign anyone in; they exist so the toolbox can act
/// as that person on GitHub and ask GitHub what they may see. See
/// <c>.design/github-integration.md</c>.</description></item>
/// </list>
///
/// <para>Matching keys on the provider's <em>stable</em> subject identifier —
/// the Entra object id, or the GitHub numeric user id — never on email, UPN or
/// login name: those change, the ids don't. One user may link more than one
/// external identity; each external identity maps to exactly one user (unique
/// on provider + issuer + subject).</para>
///
/// <para>Because both providers share the table, anything that means "this
/// person can sign in with Microsoft" must filter on
/// <c>Provider == EntraSignInService.ProviderName</c> — a GitHub link is not a
/// sign-in method and must not satisfy a strong-auth or Microsoft-only-policy
/// check.</para>
/// </summary>
public class UserExternalLogin
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Provider discriminator: <c>"entra"</c> or <c>"github"</c>.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// Which directory the identity lives in. For Entra that is the tenant id
    /// (a GUID string), and it is part of the unique key because object ids are
    /// only unique within a tenant. GitHub has no equivalent, so those rows
    /// store the constant <c>"github.com"</c> — the unique index keeps its
    /// shape and GitHub user ids are globally unique anyway.
    /// </summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// The provider's stable subject identifier — the Entra object id, or the
    /// GitHub numeric user id as a string.
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Display-only copy of the name the provider presented when the link was
    /// created: the email/UPN for Entra, the login for GitHub. Never used for
    /// matching — a GitHub login can be renamed under us, which is exactly why
    /// <see cref="Subject"/> is the id.
    /// </summary>
    public string DisplayIdentity { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>Last successful sign-in through this link. Null until first used, and always null on a GitHub row.</summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// GitHub only. The user-to-server access token, encrypted with the Data
    /// Protection key ring (see
    /// <c>GitHubAccessService.AccessTokenProtectionPurpose</c>). Null on Entra
    /// rows, which hold no token of ours. Losing <c>app-keys</c> means the user
    /// links again; the audit interceptor redacts this column.
    /// </summary>
    public string? AccessTokenEncrypted { get; set; }

    /// <summary>
    /// GitHub only. The refresh token that mints the next access token, same
    /// encryption. Null on Entra rows, and also null when the GitHub App does
    /// not expire user tokens — in that case the access token stands until the
    /// user revokes it.
    /// </summary>
    public string? RefreshTokenEncrypted { get; set; }

    /// <summary>
    /// GitHub only. When <see cref="AccessTokenEncrypted"/> stops working (UTC),
    /// so a refresh happens before a call rather than after a 401. Null means
    /// "does not expire" — see <see cref="RefreshTokenEncrypted"/>.
    /// </summary>
    public DateTime? AccessTokenExpiresAt { get; set; }

    /// <summary>
    /// GitHub only. Whether the user was a member of the organisation's
    /// connected GitHub organisation the last time we asked. Recorded so
    /// "why can't I see any repositories" has an answer on the Account row
    /// rather than being a silent empty list. Null when it has not been
    /// established (no connected organisation, or GitHub would not say).
    /// </summary>
    public bool? IsOrgMember { get; set; }
}
