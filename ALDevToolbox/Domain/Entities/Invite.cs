namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Admin-issued invitation to join an organisation. The plain-text token is
/// sent in the email; only its SHA-256 hash is persisted, so a database
/// snapshot does not yield usable tokens. Single-use:
/// <see cref="AcceptedAt"/> is stamped on first successful acceptance.
/// 7-day expiry per <c>.design/milestones.md</c> P4.19.
/// </summary>
public class Invite
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Lowercased email the invite was issued for.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Role the invitee will receive on acceptance.</summary>
    public UserRole Role { get; set; } = UserRole.User;

    /// <summary>Optional free-text welcome message rendered in the email body.</summary>
    public string? WelcomeMessage { get; set; }

    /// <summary>Hex-encoded SHA-256 of the token value sent to the user.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }

    /// <summary>Stamped when an admin revokes a still-pending invite.</summary>
    public DateTime? RevokedAt { get; set; }

    public int InvitedByUserId { get; set; }
    public User? InvitedByUser { get; set; }
}
