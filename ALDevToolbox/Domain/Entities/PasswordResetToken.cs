namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Why a token in <c>password_reset_tokens</c> was issued. Added in P4.19 so
/// the magic-link flow can reuse the existing single-use-hashed-token table
/// instead of growing a parallel one — different lifetime, different consumer,
/// same storage contract.
/// </summary>
public enum TokenPurpose
{
    PasswordReset,
    MagicLogin,
}

/// <summary>
/// Single-use token issued by <c>/forgot-password</c> or <c>/login/magic</c>.
/// The plain-text token is sent in the email; only its SHA-256 hash is
/// persisted, so a database snapshot does not yield usable tokens.
/// <see cref="ConsumedAt"/> is stamped on first successful use;
/// <see cref="ExpiresAt"/> is one hour after issue for resets and 15 minutes
/// for magic-link logins (see <c>.design/milestones.md</c> P4.19).
/// </summary>
public class PasswordResetToken
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Hex-encoded SHA-256 of the token value sent to the user.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>What flow this token belongs to. Defaults to <see cref="TokenPurpose.PasswordReset"/> for legacy rows.</summary>
    public TokenPurpose Purpose { get; set; } = TokenPurpose.PasswordReset;

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
}
