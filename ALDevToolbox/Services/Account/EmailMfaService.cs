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
    /// <summary>Wrong-code guesses allowed across the live challenge window before re-issue is required (#409).</summary>
    public const int MaxVerifyAttempts = 5;

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
        var now = _clock.GetUtcNow().UtcDateTime;
        var hash = HashCode(trimmed, userId);

        // All live (unconsumed, unexpired) challenges for this user. Issuance
        // doesn't invalidate prior codes, so more than one can be live within
        // the issue window; any of them is still acceptable.
        var live = await _db.PasswordResetTokens.IgnoreQueryFilters()
            .Where(t => t.UserId == userId
                        && t.Purpose == TokenPurpose.EmailMfaChallenge
                        && t.ConsumedAt == null
                        && t.ExpiresAt > now)
            .ToListAsync(ct);
        if (live.Count == 0) return false;

        // Brute-force guard: cap total wrong guesses across the live window so
        // the 6-digit space can't be walked before the codes expire. Once the
        // cap is hit the user must request a fresh challenge (the issue throttle
        // bounds how often that can happen). #409
        if (live.Sum(t => t.FailedAttempts) >= MaxVerifyAttempts) return false;

        var match = live.FirstOrDefault(t => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(t.TokenHash), Encoding.ASCII.GetBytes(hash)));
        if (match is null)
        {
            // Attribute the failure to the newest live challenge.
            live.OrderByDescending(t => t.CreatedAt).First().FailedAttempts++;
            await _db.SaveChangesAsync(ct);
            return false;
        }
        match.ConsumedAt = now;
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
