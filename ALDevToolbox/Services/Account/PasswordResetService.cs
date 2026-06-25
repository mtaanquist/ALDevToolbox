using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// Issues + redeems single-use credential-recovery tokens: password reset
/// links and magic-link sign-in. Both kinds land in <c>password_reset_tokens</c>
/// discriminated by <see cref="TokenPurpose"/>; the table name predates the
/// magic-link addition (P4.19) and we keep it for migration continuity.
/// Carved out of the original AccountService in #88.
/// </summary>
public sealed class PasswordResetService
{
    public static readonly TimeSpan ResetTokenLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan MagicLinkTokenLifetime = TimeSpan.FromMinutes(15);

    private readonly AppDbContext _db;
    private readonly AuthService _auth;
    private readonly TimeProvider _clock;

    public PasswordResetService(AppDbContext db, AuthService auth, TimeProvider clock)
    {
        _db = db;
        _auth = auth;
        _clock = clock;
    }

    /// <summary>
    /// Generates a single-use password reset token for the user with the
    /// given email. Returns the plaintext token — the caller emails it. We
    /// always return a token (even for unknown emails) so callers can render
    /// the same "if that email exists, you'll get a link" copy without
    /// branching on lookup outcome; the unknown-email token never lands in
    /// the table.
    /// </summary>
    public async Task<string?> CreatePasswordResetTokenAsync(string email, CancellationToken ct = default)
    {
        var normalised = AuthService.NormaliseEmail(email);
        var user = await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == normalised, ct);
        if (user is null || user.Status == UserStatus.Disabled)
        {
            return null;
        }

        var (raw, hash) = TokenIssuer.Issue();
        var now = _clock.GetUtcNow().UtcDateTime;
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = hash,
            Purpose = TokenPurpose.PasswordReset,
            CreatedAt = now,
            ExpiresAt = now + ResetTokenLifetime,
        });
        await _db.SaveChangesAsync(ct);
        return raw;
    }

    /// <summary>
    /// Consumes a reset token and applies the new password atomically. The
    /// row is stamped with <c>ConsumedAt</c> on success so it can't be
    /// reused. Throws <see cref="PlanValidationException"/> for expired,
    /// missing or already-consumed tokens.
    /// </summary>
    public async Task ConsumePasswordResetTokenAsync(string token, string newPassword, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        AuthService.ValidatePassword(newPassword, errors, fieldName: "Password");
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var hash = TokenIssuer.Sha256Hex(token);
        var now = _clock.GetUtcNow().UtcDateTime;
        var row = await _db.PasswordResetTokens.IgnoreQueryFilters()
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Purpose == TokenPurpose.PasswordReset, ct);
        if (row is null || row.ConsumedAt is not null || row.ExpiresAt <= now || row.User is null)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Token"] = "This reset link is no longer valid. Request a new one." });
        }
        if (row.User.Status != UserStatus.Active)
        {
            // Defensive, mirroring the magic-link consume: a token issued before
            // the account was disabled (or a status change mid-flow) must not let
            // a non-Active user complete a reset. Login already blocks Disabled,
            // but the two consume paths should be consistent. #410
            throw new PlanValidationException(new Dictionary<string, string> { ["Token"] = "This reset link is no longer valid. Request a new one." });
        }
        row.ConsumedAt = now;
        row.User.PasswordHash = _auth.HashPassword(newPassword);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Generates a single-use magic-link token for the user with the given
    /// email. Returns the plaintext token — the caller emails it. Returns
    /// <c>null</c> for unknown / disabled / pending users so the
    /// <c>/login/magic</c> page can render the same opaque "if that email
    /// exists" response regardless of outcome (no email-enumeration leak).
    /// 15-minute expiry, with the same per-email / per-IP rate-limit as
    /// password sign-in (10 per email, 30 per IP, per 15-minute window)
    /// per <c>.design/milestones.md</c> P4.19. Records every attempt — issued
    /// or not — in <c>login_attempts</c> so the rate counter is honest.
    /// </summary>
    public async Task<string?> CreateMagicLoginTokenAsync(string email, string ip, CancellationToken ct = default)
    {
        var normalised = AuthService.NormaliseEmail(email);
        var now = _clock.GetUtcNow().UtcDateTime;

        if (await _auth.IsRateLimitedAsync(normalised, ip, now, ct))
        {
            await _auth.RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return null;
        }

        var user = await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == normalised, ct);
        if (user is null || user.Status != UserStatus.Active)
        {
            await _auth.RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return null;
        }

        var (raw, hash) = TokenIssuer.Issue();
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = hash,
            Purpose = TokenPurpose.MagicLogin,
            CreatedAt = now,
            ExpiresAt = now + MagicLinkTokenLifetime,
        });
        await _db.SaveChangesAsync(ct);
        await _auth.RecordAttemptAsync(normalised, ip, succeeded: true, now, ct);
        return raw;
    }

    /// <summary>
    /// Consumes a magic-link token and returns the signed-in user. The token
    /// row is stamped with <c>ConsumedAt</c> on success so it can't be
    /// reused. Throws <see cref="PlanValidationException"/> for expired,
    /// missing, wrong-purpose or already-consumed tokens. The user's
    /// <c>LastLoginAt</c> is stamped so the magic-link path doesn't
    /// look like a stale account in <c>/admin/users</c>.
    /// </summary>
    public async Task<User> ConsumeMagicLoginTokenAsync(string token, CancellationToken ct = default)
    {
        var hash = TokenIssuer.Sha256Hex(token);
        var now = _clock.GetUtcNow().UtcDateTime;
        var row = await _db.PasswordResetTokens.IgnoreQueryFilters()
            .Include(t => t.User)
                .ThenInclude(u => u!.Organization)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Purpose == TokenPurpose.MagicLogin, ct);
        if (row is null || row.ConsumedAt is not null || row.ExpiresAt <= now || row.User is null)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Token"] = "This sign-in link is no longer valid. Request a new one." });
        }
        if (row.User.Status != UserStatus.Active)
        {
            // Defensive: status may have changed between issue and consume.
            throw new PlanValidationException(new Dictionary<string, string> { ["Token"] = "This sign-in link is no longer valid. Request a new one." });
        }
        row.ConsumedAt = now;
        row.User.LastLoginAt = now;
        await _db.SaveChangesAsync(ct);
        return row.User;
    }
}
