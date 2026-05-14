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
    public static readonly TimeSpan MagicLinkTokenLifetime = TimeSpan.FromMinutes(15);
    public const int MinPasswordLength = 12;
    public const int BcryptWorkFactor = 12;

    private static readonly Regex SlugRegex = new("^[a-z0-9-]+$", RegexOptions.Compiled);

    // Used to pay the BCrypt cost when the supplied email doesn't match a
    // user, so the response-time profile doesn't leak whether the email is
    // registered (timing oracle). Computed once per process.
    private static readonly Lazy<string> DummyPasswordHash = new(() =>
        BCrypt.Net.BCrypt.HashPassword("not-a-real-password", BcryptWorkFactor));

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
            // Audit FKs are Restrict (#74). Pending users rarely have audit
            // rows pointing at them, but anonymise defensively rather than
            // gamble on the rejection failing at SaveChanges.
            await _db.Database.ExecuteSqlRawAsync(
                "UPDATE audit_log SET changed_by_user_id = NULL WHERE changed_by_user_id = {0}",
                new object[] { user.Id }, ct);
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

    /// <summary>
    /// Bulk variant of <see cref="DisableUserAsync"/>. Each user is processed
    /// in turn — a last-admin guard on one row surfaces as a per-row failure
    /// instead of halting the whole batch. See <c>.design/milestones.md</c>
    /// Milestone 20.
    /// </summary>
    public Task<BulkActionResult> BulkDisableUsersAsync(IReadOnlyList<int> userIds, int actingOrgId, CancellationToken ct = default) =>
        BulkAsync(userIds, id => DisableUserAsync(id, actingOrgId, ct), ct);

    /// <summary>Bulk variant of <see cref="EnableUserAsync"/>.</summary>
    public Task<BulkActionResult> BulkEnableUsersAsync(IReadOnlyList<int> userIds, int actingOrgId, CancellationToken ct = default) =>
        BulkAsync(userIds, id => EnableUserAsync(id, actingOrgId, ct), ct);

    /// <summary>
    /// Bulk variant of <see cref="ChangeRoleAsync"/>. Each role flip carries
    /// the same last-admin guard — failures bubble up per user so an admin can
    /// see exactly which row blocked the operation.
    /// </summary>
    public Task<BulkActionResult> BulkChangeRoleAsync(IReadOnlyList<int> userIds, UserRole newRole, int actingOrgId, CancellationToken ct = default) =>
        BulkAsync(userIds, id => ChangeRoleAsync(id, newRole, actingOrgId, ct), ct);

    /// <summary>
    /// Shared shape for the bulk-action trio (#81). Iterates ids in input
    /// order (with duplicates collapsed), runs the per-user delegate, and
    /// turns a <see cref="PlanValidationException"/> into a row failure
    /// rather than halting the whole batch.
    /// </summary>
    private async Task<BulkActionResult> BulkAsync(
        IReadOnlyList<int> userIds, Func<int, Task> op, CancellationToken ct)
    {
        var succeeded = new List<int>();
        var failures = new List<BulkActionFailure>();
        foreach (var id in userIds.Distinct())
        {
            try
            {
                await op(id);
                succeeded.Add(id);
            }
            catch (PlanValidationException ex)
            {
                failures.Add(new BulkActionFailure(id, await LookupDisplayNameAsync(id, ct), ex.Errors.First().Value));
            }
        }
        return new BulkActionResult(userIds.Count, succeeded, failures);
    }

    private async Task<string> LookupDisplayNameAsync(int userId, CancellationToken ct)
    {
        var name = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct);
        return name ?? $"#{userId}";
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
            // Audit FKs are Restrict (#74): anonymise referencing rows so the
            // cascade can complete. The display-name string in `changed_by`
            // is preserved so the history still reads usefully.
            await _db.Database.ExecuteSqlRawAsync(
                "UPDATE audit_log SET changed_by_user_id = NULL WHERE changed_by_user_id IN (SELECT id FROM users WHERE organization_id = {0})",
                new object[] { org.Id }, ct);
            await _db.Database.ExecuteSqlRawAsync(
                "UPDATE audit_log SET organization_id = NULL WHERE organization_id = {0}",
                new object[] { org.Id }, ct);
            _db.Organizations.Remove(org);
        }
        else
        {
            await _db.Database.ExecuteSqlRawAsync(
                "UPDATE audit_log SET changed_by_user_id = NULL WHERE changed_by_user_id = {0}",
                new object[] { user.Id }, ct);
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
        ValidatePassword(newPassword, errors, fieldName: "Password");
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
        row.ConsumedAt = now;
        row.User.PasswordHash = HashPassword(newPassword);
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
        var normalised = NormaliseEmail(email);
        var now = _clock.GetUtcNow().UtcDateTime;

        if (await IsRateLimitedAsync(normalised, ip, now, ct))
        {
            await RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
            return null;
        }

        var user = await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == normalised, ct);
        if (user is null || user.Status != UserStatus.Active)
        {
            await RecordAttemptAsync(normalised, ip, succeeded: false, now, ct);
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
        await RecordAttemptAsync(normalised, ip, succeeded: true, now, ct);
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
            IsSystem = false,
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

}
