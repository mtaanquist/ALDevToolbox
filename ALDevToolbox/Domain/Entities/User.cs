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

    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}
