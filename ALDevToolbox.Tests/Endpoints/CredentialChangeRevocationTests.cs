using System.Globalization;
using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// Issue #675: a password change or a completed reset has to end the sessions
/// and tokens someone else may be holding. These pin the three halves of that:
/// the <c>credentials_changed_at</c> stamp, the personal-access-token revoke,
/// and <see cref="CookieSessionRevalidation"/> dropping a cookie that was
/// issued before the stamp (while leaving a freshly-issued one alone).
/// </summary>
public sealed class CredentialChangeRevocationTests : IDisposable
{
    private const string Email = "victim@cronus.example";
    private const string OldPassword = "verylongpassword12345";
    private const string NewPassword = "adifferentlongpassword67890";

    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private AuthService NewAuth(AppDbContext ctx) => new(ctx, NullLogger<AuthService>.Instance, _clock);

    private AccountService NewAccounts(AppDbContext ctx) =>
        new(ctx, NewAuth(ctx),
            new SystemSettingsService(ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, _clock),
            new ALDevToolbox.Services.SingleTenant.SingleTenantModeState(false),
            NullLogger<AccountService>.Instance, _clock);

    private PasswordResetService NewPasswordReset(AppDbContext ctx) => new(ctx, NewAuth(ctx), _clock);

    [Fact]
    public async Task Changing_the_password_stamps_the_user_and_revokes_live_tokens()
    {
        var userId = await SeedUserAsync(withTokens: 2);

        await using (var ctx = _db.NewContext())
        {
            await NewAccounts(ctx).ChangePasswordAsync(userId, OldPassword, NewPassword);
        }

        await AssertRevokedAsync(userId);
    }

    [Fact]
    public async Task Completing_a_reset_stamps_the_user_and_revokes_live_tokens()
    {
        var userId = await SeedUserAsync(withTokens: 2);

        string token;
        await using (var ctx = _db.NewContext())
        {
            token = (await NewPasswordReset(ctx).CreatePasswordResetTokenAsync(Email))!;
        }
        token.Should().NotBeNull();

        await using (var ctx = _db.NewContext())
        {
            await NewPasswordReset(ctx).ConsumePasswordResetTokenAsync(token, NewPassword);
        }

        await AssertRevokedAsync(userId);
    }

    /// <summary>
    /// The attack in the issue: a cookie handed out before the password
    /// changed keeps working. It must be dropped on the first re-validation
    /// past the throttle window.
    /// </summary>
    [Fact]
    public async Task A_cookie_issued_before_the_change_is_rejected_on_the_next_revalidation()
    {
        var userId = await SeedUserAsync();
        var stolen = SignedInAt(Now);

        // The session is live and has just been re-validated.
        (await ValidateAsync(userId, stolen)).Principal.Should().NotBeNull();

        _clock.Advance(TimeSpan.FromMinutes(1));
        await using (var ctx = _db.NewContext())
        {
            await NewAccounts(ctx).ChangePasswordAsync(userId, OldPassword, NewPassword);
        }

        // Still inside the 5-minute throttle: no DB read, so no rejection yet.
        var throttled = await ValidateAsync(userId, stolen);
        throttled.Principal.Should().NotBeNull();

        _clock.Advance(CookieSessionRevalidation.ValidationInterval + TimeSpan.FromMinutes(1));
        var rechecked = await ValidateAsync(userId, stolen);
        rechecked.Principal.Should().BeNull("the cookie predates the password change");
    }

    /// <summary>
    /// The other half: the person who changed their own password (and anyone
    /// who signs in afterwards) must stay signed in.
    /// </summary>
    [Fact]
    public async Task A_cookie_issued_after_the_change_survives_revalidation()
    {
        var userId = await SeedUserAsync();

        await using (var ctx = _db.NewContext())
        {
            await NewAccounts(ctx).ChangePasswordAsync(userId, OldPassword, NewPassword);
        }

        _clock.Advance(TimeSpan.FromSeconds(1));
        var fresh = SignedInAt(Now);

        _clock.Advance(CookieSessionRevalidation.ValidationInterval + TimeSpan.FromMinutes(1));
        var result = await ValidateAsync(userId, fresh);

        result.Principal.Should().NotBeNull();
        result.ShouldRenew.Should().BeTrue();
    }

    private async Task AssertRevokedAsync(int userId)
    {
        await using var check = _db.NewContext();
        var user = await check.Users.FindAsync(userId);
        user!.CredentialsChangedAt.Should().NotBeNull();

        var tokens = check.PersonalAccessTokens.Where(p => p.UserId == userId).ToList();
        tokens.Should().HaveCount(2);
        tokens.Should().AllSatisfy(t => t.RevokedAt.Should().NotBeNull());
    }

    /// <summary>Auth properties as a sign-in at <paramref name="issuedAt"/> would leave them.</summary>
    private static AuthenticationProperties SignedInAt(DateTime issuedAt)
    {
        var props = new AuthenticationProperties { IsPersistent = true };
        props.Items[EndpointHelpers.SignedInAtKey] = issuedAt.ToString("o", CultureInfo.InvariantCulture);
        return props;
    }

    private async Task<CookieValidatePrincipalContext> ValidateAsync(int userId, AuthenticationProperties properties)
    {
        var ctx = _db.NewContext();
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(_clock)
            .AddSingleton(ctx)
            .AddSingleton<IAuthenticationService, NoOpAuthenticationService>()
            .BuildServiceProvider();

        var http = new DefaultHttpContext { RequestServices = services };
        var identity = new ClaimsIdentity(
            [new Claim(HttpOrganizationContext.UserIdClaim, userId.ToString(CultureInfo.InvariantCulture))],
            CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity), properties, CookieAuthenticationDefaults.AuthenticationScheme);

        var context = new CookieValidatePrincipalContext(
            http,
            new AuthenticationScheme(
                CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            ticket);

        await CookieSessionRevalidation.ValidateAsync(context);
        // The properties bag is shared with the ticket, so the caller can read
        // back the throttle stamp this call wrote.
        await ctx.DisposeAsync();
        await services.DisposeAsync();
        return context;
    }

    private async Task<int> SeedUserAsync(int withTokens = 0)
    {
        await using var seed = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = Email,
            DisplayName = "Victim",
            PasswordHash = NewAuth(seed).HashPassword(OldPassword),
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = Now,
        };
        seed.Users.Add(user);
        await seed.SaveChangesAsync();

        for (var i = 0; i < withTokens; i++)
        {
            seed.PersonalAccessTokens.Add(new PersonalAccessToken
            {
                OrganizationId = TestDb.DefaultOrgId,
                UserId = user.Id,
                Name = $"token-{i}",
                TokenPrefix = $"aldt_pat_{i}",
                TokenHash = $"hash-{i}",
                CreatedAt = Now,
            });
        }
        await seed.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>
    /// <c>SignOutAsync</c> on a bare <see cref="DefaultHttpContext"/> needs an
    /// authentication service; the revalidation only calls sign-out, and the
    /// assertion is on <c>RejectPrincipal</c>, so a no-op is enough.
    /// </summary>
    private sealed class NoOpAuthenticationService : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;
    }
}
