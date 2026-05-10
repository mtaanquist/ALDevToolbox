using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Result of a /login attempt. Discriminates the failure modes the UI cares
/// about so the page can render a specific (but never enumerating) message.
/// </summary>
public enum LoginOutcome
{
    Success,
    InvalidCredentials,
    Pending,
    Disabled,
    LockedOut,
    RateLimited,
}

/// <summary>
/// Outcome of a /signup attempt.
/// </summary>
public enum SignupOutcome
{
    PendingApproval,
    EmailAlreadyTaken,
    OrganizationProvisioned,
    Invalid,
}

/// <summary>
/// Owner of every operation that touches the account / signup / reset surface.
/// Queries explicitly bypass the org query filter (login is org-aware
/// <em>after</em> we know which user it is) and re-apply org scoping by hand.
/// Hashes use BCrypt with a work factor of 12; passwords are validated against
/// the policy in <see cref="ValidatePassword"/>.
///
/// Login hardening: per-email and per-IP rate limits enforced via
/// <c>login_attempts</c> rows and a 15-minute lockout after five consecutive
/// failures. See <c>.design/auth-and-audit.md</c>.
/// </summary>
public sealed class AccountService
{
    public const int MaxAttemptsPerEmail = 10;
    public const int MaxAttemptsPerIp = 30;
    public const int LockoutThreshold = 5;
    public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ResetTokenLifetime = TimeSpan.FromHours(1);
    public const int MinPasswordLength = 12;
    public const int BcryptWorkFactor = 12;

    private static readonly Regex SlugRegex = new("^[a-z0-9-]+$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly ILogger<AccountService> _logger;
    private readonly TimeProvider _clock;

    public AccountService(AppDbContext db, ILogger<AccountService> logger, TimeProvider clock)
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

        var user = await _db.Users
            .IgnoreQueryFilters()
            .Include(u => u.Organization)
            .FirstOrDefaultAsync(u => u.Email == normalised, ct);

        if (user is null || !VerifyPassword(password, user.PasswordHash))
        {
            await RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return (LoginOutcome.InvalidCredentials, null);
        }

        if (await IsLockedOutAsync(normalised, now, ct))
        {
            await RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return (LoginOutcome.LockedOut, null);
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

        user.LastLoginAt = now;
        await _db.SaveChangesAsync(ct);
        await RecordAttemptAsync(normalised, ip, succeeded: true, now, ct);

        return (LoginOutcome.Success, user);
    }

    /// <summary>
    /// Creates a pending user (and signup request) for an existing or
    /// brand-new organisation. The slug discriminator: empty / unmatched
    /// creates a new pending org and a single user inside it; matched
    /// attaches the user to the existing org as a Pending User awaiting
    /// approval. Caller must not have already validated form input.
    /// </summary>
    public async Task<(SignupOutcome Outcome, User? User, Organization? Organization)> SignupAsync(
        string email, string displayName, string password, string? organizationSlug, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        ValidateEmail(email, errors);
        ValidateDisplayName(displayName, errors);
        ValidatePassword(password, errors);
        var slug = (organizationSlug ?? string.Empty).Trim().ToLowerInvariant();
        if (slug.Length > 0 && !SlugRegex.IsMatch(slug))
        {
            errors["OrganizationSlug"] = "Slug must use lowercase letters, digits and hyphens only.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var normalised = NormaliseEmail(email);
        var existingUser = await _db.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email == normalised, ct);
        if (existingUser is not null)
        {
            return (SignupOutcome.EmailAlreadyTaken, null, null);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        Organization org;
        bool createdNewOrg = false;
        if (slug.Length == 0)
        {
            var fallback = string.IsNullOrWhiteSpace(displayName)
                ? "org-" + Guid.NewGuid().ToString("N")[..8]
                : Slugify(displayName);
            org = await CreatePendingOrganizationAsync(fallback, displayName.Trim(), now, ct);
            createdNewOrg = true;
        }
        else
        {
            var match = await _db.Organizations
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(o => o.Slug == slug, ct);
            if (match is null)
            {
                org = await CreatePendingOrganizationAsync(slug, displayName.Trim(), now, ct);
                createdNewOrg = true;
            }
            else
            {
                org = match;
            }
        }

        var user = new User
        {
            OrganizationId = org.Id,
            Email = normalised,
            PasswordHash = HashPassword(password),
            DisplayName = displayName.Trim(),
            // New orgs auto-approve their first signup: there's no admin
            // in-org to do the approving and we deliberately have no superuser
            // (see .design/auth-and-audit.md). Existing-org signups still
            // wait on a same-org admin via /admin/users.
            Role = createdNewOrg ? UserRole.Admin : UserRole.User,
            Status = createdNewOrg ? UserStatus.Active : UserStatus.Pending,
            CreatedAt = now,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        var request = new SignupRequest
        {
            OrganizationId = org.Id,
            UserId = user.Id,
            Email = normalised,
            RequestedAt = now,
            // Auto-approval still writes a SignupRequest so /admin/users keeps
            // a complete history; the decided fields point back at the user
            // themselves so the audit log is unambiguous.
            Decision = createdNewOrg ? SignupDecision.Approved : SignupDecision.Pending,
            DecidedAt = createdNewOrg ? now : null,
            DecidedByUserId = createdNewOrg ? user.Id : null,
        };
        _db.SignupRequests.Add(request);
        if (createdNewOrg)
        {
            org.IsPending = false;
        }
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Signup recorded for {Email} into {OrgSlug} (new={New}).",
            normalised, org.Slug, createdNewOrg);

        return (createdNewOrg ? SignupOutcome.OrganizationProvisioned : SignupOutcome.PendingApproval,
                user, org);
    }

    /// <summary>
    /// Approves a pending signup. The user transitions to <c>Active</c>; the
    /// signup request gets stamped with <paramref name="decidedByUserId"/>.
    /// Caller's organisation must match the request's organisation.
    /// </summary>
    public async Task ApproveSignupAsync(int signupRequestId, int decidedByUserId, int actingOrgId, CancellationToken ct = default)
    {
        var req = await LoadSignupRequestAsync(signupRequestId, actingOrgId, ct);
        if (req.Decision != SignupDecision.Pending || req.UserId is null)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Decision"] = "This request has already been decided." });
        }
        var user = await _db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == req.UserId.Value, ct);
        user.Status = UserStatus.Active;
        req.Decision = SignupDecision.Approved;
        req.DecidedAt = _clock.GetUtcNow().UtcDateTime;
        req.DecidedByUserId = decidedByUserId;
        // The org carrying the new user becomes non-pending the moment its
        // first signup is approved. Subsequent admin login triggers the
        // first-time seed (see Program.cs bootstrap path).
        var org = await _db.Organizations.IgnoreQueryFilters().FirstAsync(o => o.Id == req.OrganizationId, ct);
        org.IsPending = false;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Rejects a pending signup. The user row is deleted; the request keeps the audit trail.</summary>
    public async Task RejectSignupAsync(int signupRequestId, int decidedByUserId, int actingOrgId, CancellationToken ct = default)
    {
        var req = await LoadSignupRequestAsync(signupRequestId, actingOrgId, ct);
        if (req.Decision != SignupDecision.Pending)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Decision"] = "This request has already been decided." });
        }
        if (req.UserId is int userId)
        {
            var user = await _db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
            _db.Users.Remove(user);
        }
        req.Decision = SignupDecision.Rejected;
        req.DecidedAt = _clock.GetUtcNow().UtcDateTime;
        req.DecidedByUserId = decidedByUserId;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Disables an active user (or pending one). Last admin protection enforced.</summary>
    public async Task DisableUserAsync(int userId, int actingOrgId, CancellationToken ct = default)
    {
        var user = await LoadUserAsync(userId, actingOrgId, ct);
        if (user.Role == UserRole.Admin && user.Status == UserStatus.Active
            && await CountActiveAdminsAsync(actingOrgId, ct) <= 1)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["LastAdmin"] = "You can't disable the last active admin in this organisation." });
        }
        user.Status = UserStatus.Disabled;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Re-enables a disabled user without further admin approval.</summary>
    public async Task EnableUserAsync(int userId, int actingOrgId, CancellationToken ct = default)
    {
        var user = await LoadUserAsync(userId, actingOrgId, ct);
        if (user.Status == UserStatus.Disabled) user.Status = UserStatus.Active;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Promotes / demotes a user. Last-admin protection on demotion.</summary>
    public async Task ChangeRoleAsync(int userId, UserRole newRole, int actingOrgId, CancellationToken ct = default)
    {
        var user = await LoadUserAsync(userId, actingOrgId, ct);
        if (user.Role == UserRole.Admin && newRole == UserRole.User
            && user.Status == UserStatus.Active
            && await CountActiveAdminsAsync(actingOrgId, ct) <= 1)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["LastAdmin"] = "You can't demote the last active admin in this organisation." });
        }
        user.Role = newRole;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Self-service password change. Verifies the current password before applying the new one.</summary>
    public async Task ChangePasswordAsync(int userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await _db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        if (!VerifyPassword(currentPassword, user.PasswordHash))
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["CurrentPassword"] = "Current password is incorrect." });
        }
        var errors = new Dictionary<string, string>();
        ValidatePassword(newPassword, errors, fieldName: "NewPassword");
        if (errors.Count > 0) throw new PlanValidationException(errors);
        user.PasswordHash = HashPassword(newPassword);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Self-service display-name change.</summary>
    public async Task ChangeDisplayNameAsync(int userId, string newDisplayName, CancellationToken ct = default)
    {
        var user = await _db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        var errors = new Dictionary<string, string>();
        ValidateDisplayName(newDisplayName, errors);
        if (errors.Count > 0) throw new PlanValidationException(errors);
        user.DisplayName = newDisplayName.Trim();
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Removes the user from their organisation. If they're the last active
    /// admin, requires <paramref name="acceptOrgDeletion"/> to be true; the
    /// org is then cascaded away with all its content. Otherwise the user
    /// row is deleted and the org keeps running.
    /// </summary>
    public async Task DeleteAccountAsync(int userId, bool acceptOrgDeletion, CancellationToken ct = default)
    {
        var user = await _db.Users.IgnoreQueryFilters()
            .Include(u => u.Organization)
            .FirstAsync(u => u.Id == userId, ct);

        var isLastActiveAdmin = user.Role == UserRole.Admin
            && user.Status == UserStatus.Active
            && await CountActiveAdminsAsync(user.OrganizationId, ct) <= 1;

        if (isLastActiveAdmin)
        {
            if (!acceptOrgDeletion)
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["LastAdmin"] = "You're the last active admin. Promote another user first or accept that the organisation will be deleted."
                });
            }
            var org = await _db.Organizations.IgnoreQueryFilters().FirstAsync(o => o.Id == user.OrganizationId, ct);
            _db.Organizations.Remove(org);
        }
        else
        {
            _db.Users.Remove(user);
        }
        await _db.SaveChangesAsync(ct);
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
        var normalised = NormaliseEmail(email);
        var user = await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == normalised, ct);
        if (user is null || user.Status == UserStatus.Disabled)
        {
            return null;
        }

        var rawBytes = RandomNumberGenerator.GetBytes(32);
        var raw = Convert.ToHexString(rawBytes).ToLowerInvariant();
        var hash = Sha256Hex(raw);
        var now = _clock.GetUtcNow().UtcDateTime;
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = hash,
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
        ValidatePassword(newPassword, errors, fieldName: "Password");
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var hash = Sha256Hex(token);
        var now = _clock.GetUtcNow().UtcDateTime;
        var row = await _db.PasswordResetTokens.IgnoreQueryFilters()
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (row is null || row.ConsumedAt is not null || row.ExpiresAt <= now || row.User is null)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Token"] = "This reset link is no longer valid. Request a new one." });
        }
        row.ConsumedAt = now;
        row.User.PasswordHash = HashPassword(newPassword);
        await _db.SaveChangesAsync(ct);
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

    private static void ValidateEmail(string? value, Dictionary<string, string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('@') || value.Length > 254)
        {
            errors["Email"] = "Enter a valid email address.";
        }
    }

    private static void ValidateDisplayName(string? value, Dictionary<string, string> errors)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length is < 2 or > 80)
        {
            errors["DisplayName"] = "Display name must be 2–80 characters.";
        }
    }

    private static string NormaliseEmail(string email) => email.Trim().ToLowerInvariant();

    private async Task<bool> IsRateLimitedAsync(string email, string ip, DateTime now, CancellationToken ct)
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

    private async Task RecordAttemptAsync(string email, string ip, bool succeeded, DateTime timestamp, CancellationToken ct)
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

    private async Task<int> CountActiveAdminsAsync(int orgId, CancellationToken ct)
    {
        return await _db.Users.IgnoreQueryFilters()
            .CountAsync(u => u.OrganizationId == orgId
                             && u.Role == UserRole.Admin
                             && u.Status == UserStatus.Active, ct);
    }

    private async Task<User> LoadUserAsync(int userId, int actingOrgId, CancellationToken ct)
    {
        var user = await _db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || user.OrganizationId != actingOrgId)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["UserId"] = "User not found in this organisation." });
        }
        return user;
    }

    private async Task<SignupRequest> LoadSignupRequestAsync(int id, int actingOrgId, CancellationToken ct)
    {
        var req = await _db.SignupRequests
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (req is null || req.OrganizationId != actingOrgId)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["RequestId"] = "Request not found in this organisation." });
        }
        return req;
    }

    private async Task<Organization> CreatePendingOrganizationAsync(string slug, string displayName, DateTime now, CancellationToken ct)
    {
        var safeSlug = Slugify(slug);
        var candidate = safeSlug;
        var disambiguator = 1;
        while (await _db.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Slug == candidate, ct))
        {
            disambiguator++;
            candidate = $"{safeSlug}-{disambiguator}";
        }
        var org = new Organization
        {
            Name = string.IsNullOrWhiteSpace(displayName) ? candidate : displayName,
            Slug = candidate,
            IsPending = true,
            IsSeeded = false,
            CreatedAt = now,
        };
        _db.Organizations.Add(org);
        await _db.SaveChangesAsync(ct);
        return org;
    }

    private static string Slugify(string value)
    {
        var lower = value.Trim().ToLowerInvariant();
        var chars = lower.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var hyphenated = new string(chars).Trim('-');
        while (hyphenated.Contains("--")) hyphenated = hyphenated.Replace("--", "-");
        return hyphenated.Length == 0 ? "org" : hyphenated;
    }

    private static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
