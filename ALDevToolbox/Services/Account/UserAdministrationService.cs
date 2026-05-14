using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
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
}
