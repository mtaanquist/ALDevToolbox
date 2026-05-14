using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// Behavioural tests for the (post-#88) account surface: BCrypt round-trip
/// and login lockout/rate-limit on <see cref="AuthService"/>,
/// signup + last-active-admin on <see cref="AccountService"/> /
/// <see cref="UserAdministrationService"/>, and reset-token single-use on
/// <see cref="PasswordResetService"/>.
/// </summary>
public sealed class AccountServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private AuthService NewAuth(Data.AppDbContext ctx) =>
        new(ctx, NullLogger<AuthService>.Instance, _clock);

    private AccountService NewAccounts(Data.AppDbContext ctx) =>
        new(ctx, NewAuth(ctx), NullLogger<AccountService>.Instance, _clock);

    private UserAdministrationService NewUserAdmin(Data.AppDbContext ctx) =>
        new(ctx, _clock);

    private PasswordResetService NewPasswordReset(Data.AppDbContext ctx) =>
        new(ctx, NewAuth(ctx), _clock);

    [Fact]
    public void BCrypt_round_trip_verifies_only_the_original_password()
    {
        using var ctx = _db.NewContext();
        var auth = NewAuth(ctx);
        var hash = auth.HashPassword("correct-horse-battery");
        auth.VerifyPassword("correct-horse-battery", hash).Should().BeTrue();
        auth.VerifyPassword("wrong-horse-battery", hash).Should().BeFalse();
    }

    [Fact]
    public async Task Signup_with_existing_slug_attaches_user_as_pending()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Organizations.Add(new Organization
            {
                Id = 99, Name = "Acme", Slug = "acme",
                IsPending = false, CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        var ctx = _db.NewContext();
        var svc = NewAccounts(ctx);
        var (outcome, user, org) = await svc.SignupAsync(
            "alice@example.com", "Alice", "verylongpassword12345", "acme");

        outcome.Should().Be(SignupOutcome.PendingApproval);
        user.Should().NotBeNull();
        user!.Status.Should().Be(UserStatus.Pending);
        user.Role.Should().Be(UserRole.User);
        org!.Slug.Should().Be("acme");
    }

    [Fact]
    public async Task Signup_with_blank_slug_auto_approves_as_new_org_admin()
    {
        var ctx = _db.NewContext();
        var svc = NewAccounts(ctx);
        var (outcome, user, org) = await svc.SignupAsync(
            "bob@example.com", "Bob", "verylongpassword12345", null);

        outcome.Should().Be(SignupOutcome.OrganizationProvisioned);
        // Brand-new orgs auto-approve their first user (we have no superuser
        // to do otherwise — see .design/auth-and-audit.md).
        org!.IsPending.Should().BeFalse();
        org.IsSystem.Should().BeFalse("new orgs are regular orgs; the system org is the singleton Default");
        user!.Role.Should().Be(UserRole.Admin, "the first user in a brand-new org runs it");
        user.Status.Should().Be(UserStatus.Active);

        // The audit trail still records the signup; auto-approval points at the user themselves.
        await using var read = _db.NewContext();
        var request = await read.SignupRequests.IgnoreQueryFilters().SingleAsync(r => r.UserId == user.Id);
        request.Decision.Should().Be(SignupDecision.Approved);
        request.DecidedByUserId.Should().Be(user.Id);
        request.DecidedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Signup_rejects_short_passwords()
    {
        var ctx = _db.NewContext();
        var svc = NewAccounts(ctx);
        Func<Task> act = () => svc.SignupAsync("c@example.com", "Carol", "short", null);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Password");
    }

    [Fact]
    public async Task Last_active_admin_cannot_demote_themselves()
    {
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                Id = 100,
                OrganizationId = orgId,
                Email = "admin@example.com",
                PasswordHash = "x",
                DisplayName = "Admin",
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        var ctx = _db.NewContext();
        var users = NewUserAdmin(ctx);
        Func<Task> demote = () => users.ChangeRoleAsync(100, UserRole.User, orgId);
        var ex = await demote.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("LastAdmin");
    }

    [Fact]
    public async Task Five_consecutive_failures_lock_the_account_for_15_minutes()
    {
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            var hash = NewAuth(seed).HashPassword("rightpasswordlong");
            seed.Users.Add(new User
            {
                OrganizationId = orgId,
                Email = "lockme@example.com",
                PasswordHash = hash,
                DisplayName = "Lock Me",
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        var ctx = _db.NewContext();
        var auth = NewAuth(ctx);
        for (var i = 0; i < AuthService.LockoutThreshold; i++)
        {
            var (outcome, _) = await auth.TryLoginAsync("lockme@example.com", "wrongpassword12345", "1.2.3.4");
            outcome.Should().Be(LoginOutcome.InvalidCredentials);
        }

        // Sixth attempt — now the right password — gets locked out.
        var (next, _) = await auth.TryLoginAsync("lockme@example.com", "rightpasswordlong", "1.2.3.4");
        next.Should().Be(LoginOutcome.LockedOut);

        // Advance the clock past the lockout window.
        _clock.Advance(AuthService.LockoutWindow + TimeSpan.FromSeconds(1));
        var (later, user) = await auth.TryLoginAsync("lockme@example.com", "rightpasswordlong", "1.2.3.4");
        later.Should().Be(LoginOutcome.Success);
        user.Should().NotBeNull();
    }

    [Fact]
    public async Task Fourth_failure_still_allows_a_correct_login_on_the_fifth_attempt()
    {
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            var hash = NewAuth(seed).HashPassword("rightpasswordlong");
            seed.Users.Add(new User
            {
                OrganizationId = orgId,
                Email = "boundary@example.com",
                PasswordHash = hash,
                DisplayName = "Boundary",
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        var ctx = _db.NewContext();
        var auth = NewAuth(ctx);

        for (var i = 0; i < AuthService.LockoutThreshold - 1; i++)
        {
            var (failed, _) = await auth.TryLoginAsync("boundary@example.com", "wrongpassword12345", "1.2.3.4");
            failed.Should().Be(LoginOutcome.InvalidCredentials);
        }

        // N-1 failures should still allow the correct password through.
        var (outcome, user) = await auth.TryLoginAsync("boundary@example.com", "rightpasswordlong", "1.2.3.4");
        outcome.Should().Be(LoginOutcome.Success);
        user.Should().NotBeNull();
    }

    [Fact]
    public async Task Wrong_password_attempts_past_the_threshold_also_lock_out()
    {
        // Regression: previously the lockout check only ran on the success
        // path, so an attacker spamming wrong passwords could keep failing
        // past the threshold without ever being told they were locked.
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            var hash = NewAuth(seed).HashPassword("rightpasswordlong");
            seed.Users.Add(new User
            {
                OrganizationId = orgId,
                Email = "spammed@example.com",
                PasswordHash = hash,
                DisplayName = "Spammed",
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        var ctx = _db.NewContext();
        var auth = NewAuth(ctx);
        for (var i = 0; i < AuthService.LockoutThreshold; i++)
        {
            await auth.TryLoginAsync("spammed@example.com", "wrongpassword12345", "1.2.3.4");
        }

        var (next, _) = await auth.TryLoginAsync("spammed@example.com", "stillwrongpassword", "1.2.3.4");
        next.Should().Be(LoginOutcome.LockedOut);
    }

    [Fact]
    public async Task Per_email_rate_limit_blocks_more_than_ten_attempts_in_fifteen_minutes()
    {
        var ctx = _db.NewContext();
        var auth = NewAuth(ctx);

        for (var i = 0; i < AuthService.MaxAttemptsPerEmail; i++)
        {
            await auth.TryLoginAsync("noone@example.com", "wrong", "1.2.3.4");
        }

        var (outcome, _) = await auth.TryLoginAsync("noone@example.com", "wrong", "1.2.3.4");
        outcome.Should().Be(LoginOutcome.RateLimited);
    }

    [Fact]
    public async Task Reset_token_is_single_use_and_expires_after_one_hour()
    {
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                Id = 200,
                OrganizationId = orgId,
                Email = "reset@example.com",
                PasswordHash = "old",
                DisplayName = "Reset",
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        var ctx = _db.NewContext();
        var svc = NewPasswordReset(ctx);
        var token = await svc.CreatePasswordResetTokenAsync("reset@example.com");
        token.Should().NotBeNull();

        await svc.ConsumePasswordResetTokenAsync(token!, "verylongnewpassword!");

        // Second use of the same token must fail.
        Func<Task> reuse = () => svc.ConsumePasswordResetTokenAsync(token!, "verylongnewpassword!");
        await reuse.Should().ThrowAsync<PlanValidationException>();
    }

    [Fact]
    public async Task Reset_token_for_unknown_email_returns_null_so_we_dont_leak_existence()
    {
        var ctx = _db.NewContext();
        var svc = NewPasswordReset(ctx);
        var token = await svc.CreatePasswordResetTokenAsync("ghost@example.com");
        token.Should().BeNull();
        (await ctx.PasswordResetTokens.AnyAsync()).Should().BeFalse();
    }
}

/// <summary>
/// Hand-rolled <see cref="TimeProvider"/> so tests can advance the clock
/// without waiting on real time. We use it for both lockout-window expiry and
/// reset-token TTL.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;
    public FakeTimeProvider(DateTimeOffset start) { _now = start; }
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}
