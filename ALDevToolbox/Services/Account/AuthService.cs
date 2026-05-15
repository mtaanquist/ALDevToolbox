using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// Verifies email + password pairs and owns the rate-limit / lockout / login-
/// attempt accounting that backs both <see cref="ALDevToolbox.Services.AccountService"/>
/// (the catch-all account surface) and <see cref="PasswordResetService"/> (the
/// magic-link flow). Carved out of the original AccountService in #88 so the
/// security-sensitive part of the auth surface can be reviewed in isolation.
/// </summary>
public sealed class AuthService
{
    public const int MaxAttemptsPerEmail = 10;
    public const int MaxAttemptsPerIp = 30;
    public const int LockoutThreshold = 5;
    public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(15);
    public const int MinPasswordLength = 12;
    public const int BcryptWorkFactor = 12;

    // Used to pay the BCrypt cost when the supplied email doesn't match a
    // user, so the response-time profile doesn't leak whether the email is
    // registered (timing oracle). Computed once per process.
    private static readonly Lazy<string> DummyPasswordHash = new(() =>
        BCrypt.Net.BCrypt.HashPassword("not-a-real-password", BcryptWorkFactor));

    private readonly AppDbContext _db;
    private readonly ILogger<AuthService> _logger;
    private readonly TimeProvider _clock;

    public AuthService(AppDbContext db, ILogger<AuthService> logger, TimeProvider clock)
    {
        _db = db;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>
    /// Verifies the email + password pair and returns the matching user when
    /// successful, along with the discriminated outcome. Records a row in
    /// <c>login_attempts</c> for both successes and failures so the rate
    /// limit and lockout windows have raw material.
    /// </summary>
    public async Task<(LoginOutcome Outcome, User? User)> TryLoginAsync(
        string email, string password, string ip, CancellationToken ct = default)
    {
        var normalised = NormaliseEmail(email);
        var now = _clock.GetUtcNow().UtcDateTime;

        if (await IsRateLimitedAsync(normalised, ip, now, ct))
        {
            await RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            _logger.LogInformation("Login throttled for {Email} from {Ip}.", normalised, ip);
            return (LoginOutcome.RateLimited, null);
        }

        // Lockout is checked before password verification so that an attacker
        // hammering the same email with wrong passwords also gets locked out,
        // not just somebody who finally guesses the right one. The current
        // attempt is not yet recorded, so IsLockedOutAsync counts strictly
        // prior attempts (LockoutThreshold consecutive failures → next
        // attempt locked).
        if (await IsLockedOutAsync(normalised, now, ct))
        {
            await RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return (LoginOutcome.LockedOut, null);
        }

        var user = await _db.Users
            .IgnoreQueryFilters()
            .Include(u => u.Organization)
            .FirstOrDefaultAsync(u => u.Email == normalised, ct);

        if (user is null)
        {
            // Pay the BCrypt cost against a dummy hash so the response time
            // doesn't leak whether the email is registered (timing oracle).
            _ = VerifyPassword(password, DummyPasswordHash.Value);
            await RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return (LoginOutcome.InvalidCredentials, null);
        }

        if (!VerifyPassword(password, user.PasswordHash))
        {
            await RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return (LoginOutcome.InvalidCredentials, null);
        }

        if (user.Status == UserStatus.Pending)
        {
            await RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return (LoginOutcome.Pending, null);
        }

        if (user.Status == UserStatus.Disabled)
        {
            await RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return (LoginOutcome.Disabled, null);
        }

        // Password is right and the account is in good standing. If the user
        // has 2FA enrolled, stop here: the caller redirects to /login/challenge
        // with a short-lived signed cookie carrying the user id. LastLoginAt
        // and the login_attempts success row are stamped by CompleteMfaAsync
        // once the second factor verifies.
        if (user.TotpEnabled || user.EmailMfaEnabled)
        {
            return (LoginOutcome.MfaRequired, user);
        }

        user.LastLoginAt = now;
        await _db.SaveChangesAsync(ct);
        await RecordAttemptAsync(normalised, ip, succeeded: true, now, ct);

        return (LoginOutcome.Success, user);
    }

    /// <summary>
    /// Finalises a multi-factor login after the second factor (TOTP / email
    /// code / recovery code) has been verified. Stamps <c>LastLoginAt</c> and
    /// records the success in <c>login_attempts</c>. The caller sets the auth
    /// cookie.
    /// </summary>
    public async Task<User> CompleteMfaAsync(int userId, string ip, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var user = await _db.Users.IgnoreQueryFilters()
            .Include(u => u.Organization)
            .FirstAsync(u => u.Id == userId, ct);
        user.LastLoginAt = now;
        await _db.SaveChangesAsync(ct);
        await RecordAttemptAsync(user.Email, ip, succeeded: true, now, ct);
        return user;
    }

    public string HashPassword(string password) => BCrypt.Net.BCrypt.HashPassword(password, BcryptWorkFactor);

    public bool VerifyPassword(string candidate, string hash)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        try
        {
            return BCrypt.Net.BCrypt.Verify(candidate, hash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }

    public static void ValidatePassword(string? value, Dictionary<string, string> errors, string fieldName = "Password")
    {
        if (string.IsNullOrEmpty(value) || value.Length < MinPasswordLength)
        {
            errors[fieldName] = $"Password must be at least {MinPasswordLength} characters.";
        }
    }

    public static string NormaliseEmail(string email) => email.Trim().ToLowerInvariant();

    public async Task<bool> IsRateLimitedAsync(string email, string ip, DateTime now, CancellationToken ct)
    {
        var window = now - RateWindow;
        var perEmail = await _db.LoginAttempts
            .IgnoreQueryFilters()
            .CountAsync(a => a.Email == email && a.Timestamp >= window, ct);
        if (perEmail >= MaxAttemptsPerEmail) return true;
        if (string.IsNullOrEmpty(ip)) return false;
        var perIp = await _db.LoginAttempts
            .IgnoreQueryFilters()
            .CountAsync(a => a.Ip == ip && a.Timestamp >= window, ct);
        return perIp >= MaxAttemptsPerIp;
    }

    /// <summary>
    /// True when the email has had <c>LockoutThreshold</c> consecutive
    /// failures within the lockout window with no intervening success.
    /// </summary>
    private async Task<bool> IsLockedOutAsync(string email, DateTime now, CancellationToken ct)
    {
        var window = now - LockoutWindow;
        var recent = await _db.LoginAttempts
            .IgnoreQueryFilters()
            .Where(a => a.Email == email && a.Timestamp >= window)
            .OrderByDescending(a => a.Timestamp)
            .Take(LockoutThreshold)
            .ToListAsync(ct);
        return recent.Count >= LockoutThreshold && recent.All(a => !a.Succeeded);
    }

    public async Task RecordAttemptAsync(string email, string ip, bool succeeded, DateTime timestamp, CancellationToken ct)
    {
        _db.LoginAttempts.Add(new LoginAttempt
        {
            Email = email,
            Ip = ip ?? string.Empty,
            Succeeded = succeeded,
            Timestamp = timestamp,
        });
        await _db.SaveChangesAsync(ct);
    }
}
