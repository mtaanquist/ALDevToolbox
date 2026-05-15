namespace ALDevToolbox.Domain.Entities;

/// <summary>Roles within an organisation. There is no superuser in v1.</summary>
public enum UserRole
{
    User,
    Admin,
}

/// <summary>
/// Lifecycle state of a <see cref="User"/>. Pending users cannot sign in;
/// disabled users cannot sign in either but stay in the audit history.
/// </summary>
public enum UserStatus
{
    Pending,
    Active,
    Disabled,
}

/// <summary>
/// One row per signed-up account. Belongs to a single
/// <see cref="Organization"/>; cross-org access is blocked.
/// </summary>
public class User
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Login identifier; lowercased on save.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>BCrypt hash; opaque to everything outside <c>AccountService</c>.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Free-text label rendered in the audit log and the top bar.</summary>
    public string DisplayName { get; set; } = string.Empty;

    public UserRole Role { get; set; }
    public UserStatus Status { get; set; }

    /// <summary>
    /// Hosting-operator flag, distinct from the per-org
    /// <see cref="UserRole.Admin"/> role. Granted explicitly via
    /// <c>/site-admin/users</c> (or by being the bootstrap admin on a
    /// fresh DB) and surfaced as a separate cookie claim.
    /// </summary>
    public bool IsSiteAdmin { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    /// <summary>True once a confirmed TOTP authenticator has been enrolled. Short-circuits the login challenge check without a join.</summary>
    public bool TotpEnabled { get; set; }

    /// <summary>True once the user has confirmed they can read codes sent to <see cref="Email"/>.</summary>
    public bool EmailMfaEnabled { get; set; }

    /// <summary>
    /// New email address waiting on confirmation from the new mailbox (admin-
    /// initiated, see <c>UserAdministrationService.RequestEmailChangeAsync</c>).
    /// Cleared back to <c>null</c> once the confirmation token is consumed.
    /// </summary>
    public string? PendingEmail { get; set; }

    public DateTime? PendingEmailAt { get; set; }
}
