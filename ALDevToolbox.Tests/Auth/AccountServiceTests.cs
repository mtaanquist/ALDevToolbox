using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// Behavioural tests for <see cref="AccountService"/>: BCrypt round-trip,
/// signup flow, last-active-admin protection, lockout / rate-limit logic
/// against a fake clock, and password reset token single-use semantics.
/// </summary>
public sealed class AccountServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    [Fact]
    public void BCrypt_round_trip_verifies_only_the_original_password()
    {
        using var ctx = _db.NewContext();
        var svc = new AccountService(ctx, NullLogger<AccountService>.Instance, _clock);
        var hash = svc.HashPassword("correct-horse-battery");
        svc.VerifyPassword("correct-horse-battery", hash).Should().BeTrue();
        svc.VerifyPassword("wrong-horse-battery", hash).Should().BeFalse();
    }

    [Fact]
    public async Task Signup_with_existing_slug_attaches_user_as_pending()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Organizations.Add(new Organization
            {
                Id = 99, Name = "Acme", Slug = "acme",
                IsPending = false, IsSeeded = true, CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        var ctx = _db.NewContext();
        var svc = new AccountService(ctx, NullLogger<AccountService>.Instance, _clock);
        var (outcome, user, org) = await svc.SignupAsync(
            "alice@example.com", "Alice", "verylongpassword12345", "acme");

        outcome.Should().Be(SignupOutcome.PendingApproval);
        user.Should().NotBeNull();
        user!.Status.Should().Be(UserStatus.Pending);
        user.Role.Should().Be(UserRole.User);
        org!.Slug.Should().Be("acme");
    }

    [Fact]
    public async Task Signup_with_blank_slug_creates_a_new_pending_org_with_admin_role()
    {
        var ctx = _db.NewContext();
        var svc = new AccountService(ctx, NullLogger<AccountService>.Instance, _clock);
        var (outcome, user, org) = await svc.SignupAsync(
            "bob@example.com", "Bob", "verylongpassword12345", null);

        outcome.Should().Be(SignupOutcome.OrganizationProvisioned);
        org!.IsPending.Should().BeTrue();
        org.IsSeeded.Should().BeFalse();
        user!.Role.Should().Be(UserRole.Admin, "the first user in a brand-new org runs it");
        user.Status.Should().Be(UserStatus.Pending);
    }

    [Fact]
    public async Task Signup_rejects_short_passwords()
    {
        var ctx = _db.NewContext();
        var svc = new AccountService(ctx, NullLogger<AccountService>.Instance, _clock);
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
        var svc = new AccountService(ctx, NullLogger<AccountService>.Instance, _clock);
        Func<Task> demote = () => svc.ChangeRoleAsync(100, UserRole.User, orgId);
        var ex = await demote.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("LastAdmin");
    }

    [Fact]
    public async Task Five_consecutive_failures_lock_the_account_for_15_minutes()
    {
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            var hash = new AccountService(seed, NullLogger<AccountService>.Instance, _clock)
                .HashPassword("rightpasswordlong");
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
        var svc = new AccountService(ctx, NullLogger<AccountService>.Instance, _clock);
        for (var i = 0; i < AccountService.LockoutThreshold; i++)
        {
            var (outcome, _) = await svc.TryLoginAsync("lockme@example.com", "wrongpassword12345", "1.2.3.4");
            outcome.Should().Be(LoginOutcome.InvalidCredentials);
        }

        // Sixth attempt — now the right password — gets locked out.
        var (next, _) = await svc.TryLoginAsync("lockme@example.com", "rightpasswordlong", "1.2.3.4");
        next.Should().Be(LoginOutcome.LockedOut);

        // Advance the clock past the lockout window.
        _clock.Advance(AccountService.LockoutWindow + TimeSpan.FromSeconds(1));
        var (later, user) = await svc.TryLoginAsync("lockme@example.com", "rightpasswordlong", "1.2.3.4");
        later.Should().Be(LoginOutcome.Success);
        user.Should().NotBeNull();
    }

    [Fact]
    public async Task Per_email_rate_limit_blocks_more_than_ten_attempts_in_fifteen_minutes()
    {
        var ctx = _db.NewContext();
        var svc = new AccountService(ctx, NullLogger<AccountService>.Instance, _clock);

        for (var i = 0; i < AccountService.MaxAttemptsPerEmail; i++)
        {
            await svc.TryLoginAsync("noone@example.com", "wrong", "1.2.3.4");
        }

        var (outcome, _) = await svc.TryLoginAsync("noone@example.com", "wrong", "1.2.3.4");
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
        var svc = new AccountService(ctx, NullLogger<AccountService>.Instance, _clock);
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
        var svc = new AccountService(ctx, NullLogger<AccountService>.Instance, _clock);
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
