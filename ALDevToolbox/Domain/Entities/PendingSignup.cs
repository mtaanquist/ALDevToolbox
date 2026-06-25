namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Pre-account email-verification record for the email-first signup flow
/// (see <c>.design/auth-and-audit.md</c>). Created when an anonymous visitor
/// enters an email at <c>/signup</c>; it carries a single-use link token and a
/// 6-digit code, both persisted only as hashes — the plaintext goes out in the
/// verification email and is never stored.
///
/// <para>
/// The row is org-less and user-less by design (it exists before any account
/// does), so it sits outside the multi-tenant query filter and is always read
/// via <c>IgnoreQueryFilters()</c> — the same trust posture as
/// <see cref="Invite"/>, <see cref="PasswordResetToken"/> and
/// <see cref="LoginAttempt"/>.
/// </para>
///
/// <para>
/// Lifecycle: <see cref="VerifiedAt"/> is stamped when the link or code checks
/// out; <see cref="CompletedAt"/> is stamped once the verified visitor finishes
/// signup and a real <see cref="User"/> is created (the single-use guard).
/// Unverified rows expire at <see cref="ExpiresAt"/> and are swept
/// opportunistically on the next <c>StartAsync</c>.
/// </para>
/// </summary>
public class PendingSignup
{
    public int Id { get; set; }

    /// <summary>Lowercased email being verified.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Hex-encoded SHA-256 of the link token sent in the email.</summary>
    public string LinkTokenHash { get; set; } = string.Empty;

    /// <summary>
    /// Hex-encoded <c>SHA-256(code + ":" + link_token_hash)</c>. Binding the
    /// short numeric code to this row's link-token hash stops the small code
    /// space being a global rainbow-table target — the same trick
    /// <see cref="Services.Account.EmailMfaService"/> uses with the user id,
    /// but the row id isn't known before insert so the link-token hash is the
    /// per-row salt.
    /// </summary>
    public string CodeHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    /// <summary>Stamped on first successful link/code verification.</summary>
    public DateTime? VerifiedAt { get; set; }

    /// <summary>Stamped when the verified visitor completes signup; single-use guard.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Count of wrong 6-digit codes submitted against this row. Capped so the
    /// small numeric code space can't be brute-forced within the row's lifetime;
    /// once it hits the cap the row is dead and the visitor must request a fresh
    /// code (the high-entropy link token is unaffected). #409
    /// </summary>
    public int FailedCodeAttempts { get; set; }
}
