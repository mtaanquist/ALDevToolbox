using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// Coverage backfill for the admin / self-service surfaces on
/// <see cref="AccountService"/> that <see cref="AccountServiceTests"/>
/// doesn't reach: approve / reject signup, singleton disable / enable /
/// change-role variants, password and display-name self-service, and the
/// account-deletion org-cascade decision matrix. Issue #70.
/// </summary>
public sealed class AccountAdministrationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    // ===== ApproveSignupAsync / RejectSignupAsync =====

    [Fact]
    public async Task Approve_marks_signup_approved_promotes_user_and_records_decider()
    {
        var (signupId, userId, _) = await SeedPendingSignupAsync(TestDb.OtherOrgId);
        var adminId = await SeedActiveAdminAsync(TestDb.OtherOrgId, "admin@example.com");

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).ApproveSignupAsync(signupId, adminId, TestDb.OtherOrgId);
        }

        await using var read = _db.NewContext();
        var req = await read.SignupRequests.IgnoreQueryFilters().FirstAsync(r => r.Id == signupId);
        req.Decision.Should().Be(SignupDecision.Approved);
        req.DecidedByUserId.Should().Be(adminId);
        req.DecidedAt.Should().NotBeNull();
        var user = await read.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId);
        user.Status.Should().Be(UserStatus.Active);
    }

    [Fact]
    public async Task Reject_marks_signup_rejected_and_removes_the_pending_user()
    {
        var (signupId, userId, _) = await SeedPendingSignupAsync(TestDb.OtherOrgId);
        var adminId = await SeedActiveAdminAsync(TestDb.OtherOrgId, "admin@example.com");

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RejectSignupAsync(signupId, adminId, TestDb.OtherOrgId);
        }

        await using var read = _db.NewContext();
        var req = await read.SignupRequests.IgnoreQueryFilters().FirstAsync(r => r.Id == signupId);
        req.Decision.Should().Be(SignupDecision.Rejected);
        (await read.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == userId))
            .Should().BeFalse("rejected signups remove the placeholder user row");
    }

    [Fact]
    public async Task Approve_refuses_when_acting_org_does_not_match_the_request()
    {
        var (signupId, _, _) = await SeedPendingSignupAsync(TestDb.OtherOrgId);
        var adminId = await SeedActiveAdminAsync(TestDb.DefaultOrgId, "admin@example.com");

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewService(ctx).ApproveSignupAsync(signupId, adminId, TestDb.DefaultOrgId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("RequestId");
    }

    [Fact]
    public async Task Approve_refuses_when_the_signup_has_already_been_decided()
    {
        var (signupId, _, _) = await SeedPendingSignupAsync(TestDb.OtherOrgId);
        var adminId = await SeedActiveAdminAsync(TestDb.OtherOrgId, "admin@example.com");
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).ApproveSignupAsync(signupId, adminId, TestDb.OtherOrgId);
        }

        await using var ctx2 = _db.NewContext();
        Func<Task> act = () => NewService(ctx2).ApproveSignupAsync(signupId, adminId, TestDb.OtherOrgId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Decision");
    }

    // ===== Disable / Enable / ChangeRole singletons =====

    [Fact]
    public async Task Disable_then_enable_round_trips_user_status()
    {
        var orgId = TestDb.OtherOrgId;
        await SeedActiveAdminAsync(orgId, "primary@example.com");
        var subjectId = await SeedActiveUserAsync(orgId, "subject@example.com", UserRole.User);

        await using (var ctx = _db.NewContext()) await NewService(ctx).DisableUserAsync(subjectId, orgId);
        await using (var read1 = _db.NewContext())
        {
            (await read1.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == subjectId)).Status
                .Should().Be(UserStatus.Disabled);
        }

        await using (var ctx = _db.NewContext()) await NewService(ctx).EnableUserAsync(subjectId, orgId);
        await using var read2 = _db.NewContext();
        (await read2.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == subjectId)).Status
            .Should().Be(UserStatus.Active);
    }

    [Fact]
    public async Task Disable_refuses_to_lock_out_the_last_active_admin()
    {
        var orgId = TestDb.OtherOrgId;
        var soloAdmin = await SeedActiveAdminAsync(orgId, "lonely@example.com");

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewService(ctx).DisableUserAsync(soloAdmin, orgId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("LastAdmin");
    }

    [Fact]
    public async Task ChangeRole_singleton_demote_refuses_to_strip_the_last_active_admin()
    {
        var orgId = TestDb.OtherOrgId;
        var soloAdmin = await SeedActiveAdminAsync(orgId, "lonely@example.com");

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewService(ctx).ChangeRoleAsync(soloAdmin, UserRole.User, orgId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("LastAdmin");
    }

    [Fact]
    public async Task Singleton_actions_refuse_to_cross_organisation_boundaries()
    {
        // Acting in DefaultOrg, target is in OtherOrg: must refuse.
        await SeedActiveAdminAsync(TestDb.OtherOrgId, "lonely@example.com");
        var subjectId = await SeedActiveUserAsync(TestDb.OtherOrgId, "victim@example.com", UserRole.User);

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewService(ctx).DisableUserAsync(subjectId, TestDb.DefaultOrgId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("UserId");
    }

    // ===== ChangePasswordAsync =====

    [Fact]
    public async Task Change_password_rejects_wrong_current_password()
    {
        var userId = await SeedActiveUserWithPasswordAsync(TestDb.OtherOrgId, "p@example.com", "correctpasswordlong");

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewService(ctx).ChangePasswordAsync(userId, "wrong-current", "newpasswordlong12345");
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("CurrentPassword");
    }

    [Fact]
    public async Task Change_password_rejects_new_password_that_fails_policy()
    {
        var userId = await SeedActiveUserWithPasswordAsync(TestDb.OtherOrgId, "p@example.com", "correctpasswordlong");

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewService(ctx).ChangePasswordAsync(userId, "correctpasswordlong", "short");
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("NewPassword");
    }

    [Fact]
    public async Task Change_password_happy_path_re_hashes_so_old_password_no_longer_verifies()
    {
        var userId = await SeedActiveUserWithPasswordAsync(TestDb.OtherOrgId, "p@example.com", "correctpasswordlong");

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).ChangePasswordAsync(userId, "correctpasswordlong", "newcorrectpasswordlong");
        }

        await using var read = _db.NewContext();
        var user = await read.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId);
        var svc = new AccountService(read, NullLogger<AccountService>.Instance, _clock);
        svc.VerifyPassword("newcorrectpasswordlong", user.PasswordHash).Should().BeTrue();
        svc.VerifyPassword("correctpasswordlong", user.PasswordHash).Should().BeFalse();
    }

    // ===== ChangeDisplayNameAsync =====

    [Fact]
    public async Task Change_display_name_trims_whitespace_and_rejects_too_short()
    {
        var userId = await SeedActiveUserAsync(TestDb.OtherOrgId, "name@example.com", UserRole.User);

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).ChangeDisplayNameAsync(userId, "   Padded Name   ");
        }
        await using (var read = _db.NewContext())
        {
            (await read.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId)).DisplayName
                .Should().Be("Padded Name");
        }

        await using var rejectCtx = _db.NewContext();
        Func<Task> act = () => NewService(rejectCtx).ChangeDisplayNameAsync(userId, " ");
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("DisplayName");
    }

    // ===== DeleteAccountAsync =====

    [Fact]
    public async Task Delete_account_for_a_regular_user_removes_only_the_user_row()
    {
        var orgId = TestDb.OtherOrgId;
        await SeedActiveAdminAsync(orgId, "admin@example.com");
        var subjectId = await SeedActiveUserAsync(orgId, "self@example.com", UserRole.User);

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).DeleteAccountAsync(subjectId, acceptOrgDeletion: false);
        }

        await using var read = _db.NewContext();
        (await read.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == subjectId)).Should().BeFalse();
        (await read.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Id == orgId)).Should().BeTrue();
    }

    [Fact]
    public async Task Last_active_admin_delete_without_accept_org_deletion_refuses()
    {
        var orgId = TestDb.OtherOrgId;
        var soloAdmin = await SeedActiveAdminAsync(orgId, "lonely@example.com");

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewService(ctx).DeleteAccountAsync(soloAdmin, acceptOrgDeletion: false);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("LastAdmin");

        await using var read = _db.NewContext();
        (await read.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == soloAdmin)).Should().BeTrue(
            "the user row must survive when the request is refused");
        (await read.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Id == orgId)).Should().BeTrue();
    }

    [Fact]
    public async Task Last_active_admin_delete_with_accept_cascades_the_organisation()
    {
        var orgId = TestDb.OtherOrgId;
        var soloAdmin = await SeedActiveAdminAsync(orgId, "lonely@example.com");

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).DeleteAccountAsync(soloAdmin, acceptOrgDeletion: true);
        }

        await using var read = _db.NewContext();
        (await read.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Id == orgId))
            .Should().BeFalse("accepting org deletion cascades the organisation away with its users");
    }

    // ===== Fixture helpers =====

    private AccountService NewService(Data.AppDbContext ctx) =>
        new(ctx, NullLogger<AccountService>.Instance, _clock);

    private async Task<int> SeedActiveAdminAsync(int orgId, string email) =>
        await SeedActiveUserAsync(orgId, email, UserRole.Admin);

    private async Task<int> SeedActiveUserAsync(int orgId, string email, UserRole role)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = orgId,
            Email = email,
            DisplayName = email,
            PasswordHash = "placeholder",
            Role = role,
            Status = UserStatus.Active,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    private async Task<int> SeedActiveUserWithPasswordAsync(int orgId, string email, string password)
    {
        await using var ctx = _db.NewContext();
        var svc = new AccountService(ctx, NullLogger<AccountService>.Instance, _clock);
        var user = new User
        {
            OrganizationId = orgId,
            Email = email,
            DisplayName = email,
            PasswordHash = svc.HashPassword(password),
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    private async Task<(int SignupId, int UserId, int OrgId)> SeedPendingSignupAsync(int orgId)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = orgId,
            Email = $"pending-{Guid.NewGuid():N}@example.com",
            DisplayName = "Pending",
            PasswordHash = "placeholder",
            Role = UserRole.User,
            Status = UserStatus.Pending,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();

        var req = new SignupRequest
        {
            OrganizationId = orgId,
            UserId = user.Id,
            Email = user.Email,
            RequestedAt = _clock.GetUtcNow().UtcDateTime,
            Decision = SignupDecision.Pending,
        };
        ctx.SignupRequests.Add(req);
        await ctx.SaveChangesAsync();
        return (req.Id, user.Id, orgId);
    }
}
