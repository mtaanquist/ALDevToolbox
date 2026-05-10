namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One row per /login or /forgot-password POST. Used to enforce both the
/// per-email and the per-IP rate limits and the post-failure lockout window.
/// Successful attempts are also logged so an admin can investigate "where did
/// my account get used from?".
/// </summary>
public class LoginAttempt
{
    public int Id { get; set; }

    /// <summary>Lowercased email the request claimed; may be empty if missing.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Best-effort source IP (forwarded-for aware); may be empty.</summary>
    public string Ip { get; set; } = string.Empty;

    public bool Succeeded { get; set; }

    public DateTime Timestamp { get; set; }
}
