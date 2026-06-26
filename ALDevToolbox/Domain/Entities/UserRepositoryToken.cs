using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A user's encrypted Personal Access Token for one Git hosting provider, scoped
/// to <em>(user, organisation, provider)</em>. The project-build pipeline clones a
/// repository as the user who triggered the build, resolving that user's token for
/// the repo's provider — so a build fails for whoever actually lacks access, rather
/// than silently succeeding on a shared organisation credential. Replaces the
/// per-org PATs that used to live on <c>organization_settings</c>. See
/// <c>.design/artifacts.md</c>.
/// </summary>
public class UserRepositoryToken
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Which Git host this token authenticates against.</summary>
    public RepositoryProvider Provider { get; set; }

    /// <summary>
    /// The PAT, encrypted with the Data Protection key ring (per-provider purpose
    /// string on <see cref="Services.Account.UserRepositoryTokenService"/>). Losing
    /// <c>app-keys</c> requires re-entering it. The audit interceptor redacts this
    /// column so ciphertext never lands in history.
    /// </summary>
    public string TokenEncrypted { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Last time a build resolved this token, surfaced in the account UI.</summary>
    public DateTime? LastUsedAt { get; set; }
}
