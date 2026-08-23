namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A link between a local <see cref="User"/> and an identity at an external
/// identity provider — today only Microsoft Entra ID. Matching keys on the
/// provider's <em>stable</em> subject identifier (the Entra object id), never
/// on email or UPN: emails change, object ids don't. One user may link more
/// than one external identity; each external identity maps to exactly one
/// user (unique on provider + issuer + subject). See the Entra tracking
/// issue #552 for the sign-in flow that consumes these rows.
/// </summary>
public class UserExternalLogin
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Provider discriminator. v1 stamps <c>"entra"</c> on every row.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// The Entra tenant id (GUID string) the identity lives in. Part of the
    /// unique key because object ids are only unique within a tenant.
    /// </summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>The provider's stable subject identifier — the Entra object id.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Display-only copy of the email/UPN the provider presented when the link
    /// was created. Never used for matching — admins see it next to the badge
    /// on the Users tab so they can tell which Microsoft account is linked.
    /// </summary>
    public string DisplayIdentity { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>Last successful sign-in through this link. Null until first used.</summary>
    public DateTime? LastLoginAt { get; set; }
}
