using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// Admin-of-the-org actions on existing user accounts: approving / rejecting
/// pending signups, disabling / enabling accounts, role flips, and the bulk
/// versions of each. Carved out of the original AccountService in #88 so the
/// admin user-management surface lives in one place and the security-
/// sensitive auth code (login, tokens) doesn't have to scroll past it.
/// </summary>
/// <remarks>
/// "Last active admin" is enforced consistently across every demote / disable
/// path so an org can never be left without a way back in. Every method
/// returns either successfully or via <see cref="PlanValidationException"/>
/// with field-keyed errors the UI can render inline.
/// </remarks>
public sealed class UserAdministrationService
{
    public static readonly TimeSpan EmailChangeTokenLifetime = TimeSpan.FromHours(24);

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public UserAdministrationService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
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
            // Pending users rarely have audit rows pointing at them, but
            // anonymise defensively rather than gamble on the rejection
            // failing at SaveChanges.
            await _db.AnonymiseActorAsync(user.Id, ct);
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

    /// <summary>Promotes / demotes a user. Last-admin protection on any demotion away from Admin (User or Editor).</summary>
    public async Task ChangeRoleAsync(int userId, UserRole newRole, int actingOrgId, CancellationToken ct = default)
    {
        var user = await LoadUserAsync(userId, actingOrgId, ct);
        if (user.Role == UserRole.Admin && newRole != UserRole.Admin
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

    /// <summary>
    /// Stamps a pending email change on the user row and issues a confirmation
    /// token (purpose <see cref="TokenPurpose.EmailChangeConfirm"/>, 24-hour
    /// lifetime). Returns the plaintext token — the endpoint emails it to the
    /// <em>new</em> address. The actual swap happens in
    /// <see cref="ConfirmEmailChangeAsync"/> once that mailbox responds.
    /// </summary>
    public async Task<string> RequestEmailChangeAsync(int targetUserId, string newEmail, int actingOrgId, int actingUserId, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        var normalised = (newEmail ?? string.Empty).Trim().ToLowerInvariant();
        if (!EmailAddress.HasValidShape(normalised))
        {
            errors["NewEmail"] = "Enter a valid email address.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var user = await LoadUserAsync(targetUserId, actingOrgId, ct);
        if (user.Id == actingUserId)
        {
            // Admins changing their own email via the admin route is too
            // accident-prone (they'd be racing themselves on the confirmation).
            // Self-service email change is out of scope this milestone — once
            // shipped, this user would use /account.
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["NewEmail"] = "You can't change your own email from the admin page. Ask another admin or a SiteAdmin."
            });
        }
        if (string.Equals(user.Email, normalised, StringComparison.Ordinal))
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["NewEmail"] = "That's already this user's email." });
        }
        var taken = await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == normalised && u.Id != user.Id, ct);
        if (taken)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["NewEmail"] = "Another account already uses that email." });
        }

        var (raw, hash) = TokenIssuer.Issue();
        var now = _clock.GetUtcNow().UtcDateTime;
        // Burn any still-valid prior change tokens for this user. Without this,
        // a previous recipient's link would still validate against the newly-
        // stored pending_email and silently confirm an address they never had
        // access to.
        var stale = await _db.PasswordResetTokens.IgnoreQueryFilters()
            .Where(t => t.UserId == user.Id
                        && t.Purpose == TokenPurpose.EmailChangeConfirm
                        && t.ConsumedAt == null
                        && t.ExpiresAt > now)
            .ToListAsync(ct);
        foreach (var s in stale) s.ConsumedAt = now;

        user.PendingEmail = normalised;
        user.PendingEmailAt = now;
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = hash,
            Purpose = TokenPurpose.EmailChangeConfirm,
            CreatedAt = now,
            ExpiresAt = now + EmailChangeTokenLifetime,
        });
        await _db.SaveChangesAsync(ct);
        return raw;
    }

    /// <summary>
    /// Consumes an <see cref="TokenPurpose.EmailChangeConfirm"/> token: swaps
    /// <see cref="User.Email"/> for <see cref="User.PendingEmail"/>, clears the
    /// pending fields, and returns the user. Returns <c>null</c> for an
    /// invalid / expired / consumed token without revealing which.
    /// </summary>
    public async Task<User?> ConfirmEmailChangeAsync(string rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;
        var hash = TokenIssuer.Sha256Hex(rawToken);
        var now = _clock.GetUtcNow().UtcDateTime;
        var row = await _db.PasswordResetTokens.IgnoreQueryFilters()
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Purpose == TokenPurpose.EmailChangeConfirm, ct);
        if (row is null || row.ConsumedAt is not null || row.ExpiresAt <= now || row.User is null)
        {
            return null;
        }
        var user = row.User;
        if (string.IsNullOrEmpty(user.PendingEmail))
        {
            // Pending email was cleared (admin reverted, account modified):
            // burn the token but don't change anything.
            row.ConsumedAt = now;
            await _db.SaveChangesAsync(ct);
            return null;
        }
        // Race: another account may have grabbed the address since the token
        // was issued.
        var stolen = await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == user.PendingEmail && u.Id != user.Id, ct);
        if (stolen)
        {
            user.PendingEmail = null;
            user.PendingEmailAt = null;
            row.ConsumedAt = now;
            await _db.SaveChangesAsync(ct);
            return null;
        }
        user.Email = user.PendingEmail;
        user.PendingEmail = null;
        user.PendingEmailAt = null;
        row.ConsumedAt = now;
        await _db.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>
    /// SiteAdmin-only break-glass: clears every 2FA factor for the target
    /// user (TOTP secret + recovery codes + every passkey) and flips both
    /// MFA flags off. The user can re-enroll after their next sign-in.
    /// </summary>
    public async Task ResetMfaAsync(int targetUserId, CancellationToken ct = default)
    {
        var user = await _db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == targetUserId, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string> { ["UserId"] = "User not found." });

        var totp = await _db.UserTotpSecrets.IgnoreQueryFilters()
            .Where(s => s.UserId == targetUserId).ToListAsync(ct);
        _db.UserTotpSecrets.RemoveRange(totp);
        var codes = await _db.UserRecoveryCodes.IgnoreQueryFilters()
            .Where(c => c.UserId == targetUserId).ToListAsync(ct);
        _db.UserRecoveryCodes.RemoveRange(codes);
        var passkeys = await _db.UserPasskeys.IgnoreQueryFilters()
            .Where(p => p.UserId == targetUserId).ToListAsync(ct);
        _db.UserPasskeys.RemoveRange(passkeys);
        user.TotpEnabled = false;
        user.EmailMfaEnabled = false;
        await _db.SaveChangesAsync(ct);
    }
}
