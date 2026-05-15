namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// TOTP shared secret for a user, one row per user (1:1 enforced by a unique
/// index on <see cref="UserId"/>). The Base32-encoded secret is encrypted at
/// rest via the Data Protection key ring (purpose
/// <c>ALDevToolbox.UserTotpSecret</c>); the verifier needs the plaintext to
/// compute the rolling RFC 6238 code, so encryption — not hashing — is the
/// only option. Losing the key ring means TOTP for every user is gone (same
/// blast radius as the SMTP password); recovery is via email-MFA fallback,
/// recovery codes, or a SiteAdmin reset.
///
/// A row with <see cref="ConfirmedAt"/> still <c>null</c> is a pending
/// enrollment: re-running setup just overwrites it.
/// </summary>
public class UserTotpSecret
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Base32 TOTP secret, encrypted with <c>IDataProtector</c>.</summary>
    public string SecretEncrypted { get; set; } = string.Empty;

    /// <summary>Set the first time the user submits a valid code. Until then, login does not gate on TOTP.</summary>
    public DateTime? ConfirmedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}
