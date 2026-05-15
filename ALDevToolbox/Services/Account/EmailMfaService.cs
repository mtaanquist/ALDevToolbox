using System.Security.Cryptography;
using System.Text;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// Email-based 2FA codes: 6-digit numeric, single use, 10-minute lifetime.
/// Stored hash is <c>SHA-256(code + ":" + user_id)</c>; the user-id binding
/// stops the short numeric space being a global rainbow-table target. Per-
/// user rate limit: 3 issues per 10 minutes, counted from the
/// <c>password_reset_tokens</c> rows with purpose
/// <see cref="TokenPurpose.EmailMfaChallenge"/> (survives process restart;
/// no extra in-memory state).
///
/// Separate from <see cref="PasswordResetService"/> because the storage
/// shape — short numeric + user-id-bound hash — is intentionally different.
/// </summary>
public sealed class EmailMfaService
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int MaxIssuesPerWindow = 3;
    public static readonly TimeSpan IssueWindow = TimeSpan.FromMinutes(10);

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public EmailMfaService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Generates and persists a fresh 6-digit code for the user. Returns the
    /// plaintext for the caller to email; only the user-bound SHA-256 hash
    /// lands in the database. Returns <c>null</c> when the per-user rate limit
    /// is exceeded.
    /// </summary>
    public async Task<string?> IssueChallengeAsync(int userId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var since = now - IssueWindow;
        var recent = await _db.PasswordResetTokens.IgnoreQueryFilters()
            .CountAsync(t => t.UserId == userId
                             && t.Purpose == TokenPurpose.EmailMfaChallenge
                             && t.CreatedAt >= since, ct);
        if (recent >= MaxIssuesPerWindow) return null;

        var code = GenerateCode();
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = userId,
            TokenHash = HashCode(code, userId),
            Purpose = TokenPurpose.EmailMfaChallenge,
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        });
        await _db.SaveChangesAsync(ct);
        return code;
    }

    /// <summary>
    /// Consumes a code; returns true on first valid match. Single-use is
    /// enforced via <c>ConsumedAt</c>.
    /// </summary>
    public async Task<bool> VerifyAsync(int userId, string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var trimmed = code.Replace(" ", "").Trim();
        if (trimmed.Length != 6 || !trimmed.All(char.IsDigit)) return false;
        var hash = HashCode(trimmed, userId);
        var now = _clock.GetUtcNow().UtcDateTime;
        var row = await _db.PasswordResetTokens.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.UserId == userId
                                      && t.Purpose == TokenPurpose.EmailMfaChallenge
                                      && t.TokenHash == hash, ct);
        if (row is null || row.ConsumedAt is not null || row.ExpiresAt <= now) return false;
        row.ConsumedAt = now;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Flips <see cref="User.EmailMfaEnabled"/>. Caller verifies the code first.</summary>
    public async Task EnableAsync(int userId, CancellationToken ct = default)
    {
        var user = await _db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        user.EmailMfaEnabled = true;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DisableAsync(int userId, CancellationToken ct = default)
    {
        var user = await _db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        user.EmailMfaEnabled = false;
        await _db.SaveChangesAsync(ct);
    }

    private static string GenerateCode()
    {
        // Six independent digits via a uniformly-distributed 32-bit draw per
        // digit. Using `random % 10` on a Span<byte> would skew the
        // distribution slightly; not material at this scale but cheap to do
        // right.
        Span<char> chars = stackalloc char[6];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)('0' + RandomNumberGenerator.GetInt32(0, 10));
        }
        return new string(chars);
    }

    private static string HashCode(string code, int userId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(code + ":" + userId));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
