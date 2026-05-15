namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Single-use TOTP recovery code. Ten rows are issued at TOTP enrollment;
/// each row is consumed (stamped <see cref="ConsumedAt"/>) on first valid
/// match. Stored hashed via BCrypt — the codes are weaker than passwords by
/// definition but should still resist offline brute force.
/// </summary>
public class UserRecoveryCode
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>BCrypt hash of the plaintext code shown to the user once.</summary>
    public string CodeHash { get; set; } = string.Empty;

    public DateTime? ConsumedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
