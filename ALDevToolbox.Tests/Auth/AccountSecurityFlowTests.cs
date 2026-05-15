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
/// Cross-service flows: login orchestration when 2FA is enrolled, the refined
/// self-delete guard, admin email change, and SiteAdmin MFA reset.
/// </summary>
public sealed class AccountSecurityFlowTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private AuthService NewAuth(Data.AppDbContext ctx) =>
        new(ctx, NullLogger<AuthService>.Instance, _clock);
    private AccountService NewAccounts(Data.AppDbContext ctx) =>
        new(ctx, NewAuth(ctx), NullLogger<AccountService>.Instance, _clock);
    private UserAdministrationService NewUserAdmin(Data.AppDbContext ctx) =>
        new(ctx, _clock);

    [Fact]
    public async Task TryLogin_returns_MfaRequired_when_totp_enabled()
    {
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            var auth = NewAuth(seed);
            seed.Users.Add(new User
            {
                Id = 5000, OrganizationId = orgId, Email = "mfa@example.com",
                PasswordHash = auth.HashPassword("verylongpassword12345"),
                DisplayName = "M", Role = UserRole.User, Status = UserStatus.Active,
                TotpEnabled = true, CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }
        await using var ctx = _db.NewContext();
        var (outcome, user) = await NewAuth(ctx).TryLoginAsync(
            "mfa@example.com", "verylongpassword12345", "127.0.0.1");
        outcome.Should().Be(LoginOutcome.MfaRequired);
        user.Should().NotBeNull();
        user!.LastLoginAt.Should().BeNull("Last-login stamp is deferred until CompleteMfaAsync");
    }

    [Fact]
    public async Task CompleteMfa_stamps_last_login_and_records_success()
    {
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                Id = 5010, OrganizationId = orgId, Email = "mfa2@example.com",
                PasswordHash = "x", DisplayName = "M2",
                Role = UserRole.User, Status = UserStatus.Active, TotpEnabled = true,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }
        await using (var ctx = _db.NewContext())
        {
            var auth = NewAuth(ctx);
            var user = await auth.CompleteMfaAsync(5010, "127.0.0.1");
            user.LastLoginAt.Should().NotBeNull();
        }
        await using var read = _db.NewContext();
        var attempt = await read.LoginAttempts.IgnoreQueryFilters()
            .Where(a => a.Email == "mfa2@example.com")
            .OrderByDescending(a => a.Timestamp).FirstAsync();
        attempt.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Self_delete_refuses_when_last_admin_with_other_members()
    {
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            seed.Users.AddRange(
                new User { Id = 5200, OrganizationId = orgId, Email = "a@example.com", PasswordHash = "x", DisplayName = "Admin",
                    Role = UserRole.Admin, Status = UserStatus.Active, CreatedAt = _clock.GetUtcNow().UtcDateTime },
                new User { Id = 5201, OrganizationId = orgId, Email = "b@example.com", PasswordHash = "x", DisplayName = "User",
                    Role = UserRole.User, Status = UserStatus.Active, CreatedAt = _clock.GetUtcNow().UtcDateTime });
            await seed.SaveChangesAsync();
        }
        await using var ctx = _db.NewContext();
        var svc = NewAccounts(ctx);
        var act = () => svc.DeleteAccountAsync(5200, acceptOrgDeletion: true);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("LastAdmin");
        ex.Which.Errors["LastAdmin"].Should().Contain("Promote");
    }

    [Fact]
    public async Task Self_delete_allowed_when_only_member_with_org_cascade()
    {
        var orgId = TestDb.OtherOrgId;
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                Id = 5300, OrganizationId = orgId, Email = "solo@example.com", PasswordHash = "x",
                DisplayName = "Solo", Role = UserRole.Admin, Status = UserStatus.Active,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }
        await using var ctx = _db.NewContext();
        await NewAccounts(ctx).DeleteAccountAsync(5300, acceptOrgDeletion: true);
        await using var read = _db.NewContext();
        (await read.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Id == orgId)).Should().BeFalse();
    }

    [Fact]
    public async Task Admin_email_change_persists_pending_and_confirm_swaps()
    {
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            seed.Users.AddRange(
                new User { Id = 5400, OrganizationId = orgId, Email = "admin@example.com", PasswordHash = "x",
                    DisplayName = "Admin", Role = UserRole.Admin, Status = UserStatus.Active,
                    CreatedAt = _clock.GetUtcNow().UtcDateTime },
                new User { Id = 5401, OrganizationId = orgId, Email = "old@example.com", PasswordHash = "x",
                    DisplayName = "Target", Role = UserRole.User, Status = UserStatus.Active,
                    CreatedAt = _clock.GetUtcNow().UtcDateTime });
            await seed.SaveChangesAsync();
        }
        string token;
        await using (var ctx = _db.NewContext())
        {
            token = await NewUserAdmin(ctx).RequestEmailChangeAsync(5401, "new@example.com", orgId, 5400);
        }
        await using (var read = _db.NewContext())
        {
            var user = await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == 5401);
            user.PendingEmail.Should().Be("new@example.com");
            user.Email.Should().Be("old@example.com", "until the token is consumed");
        }
        await using (var ctx = _db.NewContext())
        {
            var result = await NewUserAdmin(ctx).ConfirmEmailChangeAsync(token);
            result.Should().NotBeNull();
        }
        await using (var read = _db.NewContext())
        {
            var user = await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == 5401);
            user.Email.Should().Be("new@example.com");
            user.PendingEmail.Should().BeNull();
        }
    }

    [Fact]
    public async Task Reissued_email_change_invalidates_prior_token()
    {
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            seed.Users.AddRange(
                new User { Id = 5450, OrganizationId = orgId, Email = "admin2@example.com", PasswordHash = "x",
                    DisplayName = "Admin2", Role = UserRole.Admin, Status = UserStatus.Active,
                    CreatedAt = _clock.GetUtcNow().UtcDateTime },
                new User { Id = 5451, OrganizationId = orgId, Email = "user@example.com", PasswordHash = "x",
                    DisplayName = "User", Role = UserRole.User, Status = UserStatus.Active,
                    CreatedAt = _clock.GetUtcNow().UtcDateTime });
            await seed.SaveChangesAsync();
        }
        string firstToken, secondToken;
        await using (var ctx = _db.NewContext())
        {
            firstToken = await NewUserAdmin(ctx).RequestEmailChangeAsync(5451, "first@example.com", orgId, 5450);
        }
        await using (var ctx = _db.NewContext())
        {
            secondToken = await NewUserAdmin(ctx).RequestEmailChangeAsync(5451, "second@example.com", orgId, 5450);
        }
        await using (var ctx = _db.NewContext())
        {
            var result = await NewUserAdmin(ctx).ConfirmEmailChangeAsync(firstToken);
            result.Should().BeNull("old token must be invalidated when a newer change is requested");
        }
        await using var read = _db.NewContext();
        var user = await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == 5451);
        user.Email.Should().Be("user@example.com", "the old token must not have swapped the email");
        // Second token still works against the current pending value.
        await using (var ctx = _db.NewContext())
        {
            (await NewUserAdmin(ctx).ConfirmEmailChangeAsync(secondToken)).Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Admin_cannot_change_own_email_via_admin_path()
    {
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User { Id = 5500, OrganizationId = orgId, Email = "selfadmin@example.com",
                PasswordHash = "x", DisplayName = "A", Role = UserRole.Admin, Status = UserStatus.Active,
                CreatedAt = _clock.GetUtcNow().UtcDateTime });
            await seed.SaveChangesAsync();
        }
        await using var ctx = _db.NewContext();
        var act = () => NewUserAdmin(ctx).RequestEmailChangeAsync(5500, "fresh@example.com", orgId, 5500);
        await act.Should().ThrowAsync<PlanValidationException>();
    }

    [Fact]
    public async Task ResetMfa_wipes_secret_codes_passkeys_and_flags()
    {
        var orgId = TestDb.DefaultOrgId;
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User { Id = 5600, OrganizationId = orgId, Email = "reset@example.com",
                PasswordHash = "x", DisplayName = "R", Role = UserRole.User, Status = UserStatus.Active,
                TotpEnabled = true, EmailMfaEnabled = true,
                CreatedAt = _clock.GetUtcNow().UtcDateTime });
            seed.UserTotpSecrets.Add(new UserTotpSecret { UserId = 5600, SecretEncrypted = "x",
                ConfirmedAt = _clock.GetUtcNow().UtcDateTime, CreatedAt = _clock.GetUtcNow().UtcDateTime });
            seed.UserRecoveryCodes.Add(new UserRecoveryCode { UserId = 5600, CodeHash = "h",
                CreatedAt = _clock.GetUtcNow().UtcDateTime });
            seed.UserPasskeys.Add(new UserPasskey { UserId = 5600,
                CredentialId = new byte[] { 1, 2, 3 }, PublicKey = new byte[] { 4, 5, 6 },
                SignCounter = 1, Transports = "usb", Nickname = "Yubi",
                CreatedAt = _clock.GetUtcNow().UtcDateTime });
            await seed.SaveChangesAsync();
        }
        await using (var ctx = _db.NewContext())
        {
            await NewUserAdmin(ctx).ResetMfaAsync(5600);
        }
        await using var read = _db.NewContext();
        var user = await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == 5600);
        user.TotpEnabled.Should().BeFalse();
        user.EmailMfaEnabled.Should().BeFalse();
        (await read.UserTotpSecrets.IgnoreQueryFilters().CountAsync(s => s.UserId == 5600)).Should().Be(0);
        (await read.UserRecoveryCodes.IgnoreQueryFilters().CountAsync(c => c.UserId == 5600)).Should().Be(0);
        (await read.UserPasskeys.IgnoreQueryFilters().CountAsync(p => p.UserId == 5600)).Should().Be(0);
    }
}
