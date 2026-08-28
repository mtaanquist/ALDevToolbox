using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// Tests for local_login_policy = EntraOnly enforcement (issue #552, slice
/// 3): password/magic-link/reset refusals, the SiteAdmin break-glass
/// exemption, the enable guard, and the self-service link/unlink rules.
/// </summary>
public sealed class EntraLoginPolicyTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Password = "correct-horse-battery";
    private const string Ip = "203.0.113.10";

    private AuthService NewAuth(AppDbContext ctx) =>
        new(ctx, NullLogger<AuthService>.Instance, TimeProvider.System);

    private EntraSignInService NewEntra(AppDbContext ctx) =>
        new(ctx, NewAuth(ctx), _db.OrgContext, _db.DataProtectionProvider, TimeProvider.System,
            NullLogger<EntraSignInService>.Instance);

    private async Task SetPolicyAsync(int orgId, LocalLoginPolicy policy, bool entraEnabled = true)
    {
        await using var ctx = _db.NewContext();
        var row = await ctx.OrganizationSettings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.OrganizationId == orgId);
        if (row is null)
        {
            row = new OrganizationSettings { OrganizationId = orgId };
            ctx.OrganizationSettings.Add(row);
        }
        row.EntraEnabled = entraEnabled;
        row.EntraAllowedTenantIds = new List<string> { TenantA };
        row.LocalLoginPolicy = policy;
        row.UpdatedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();
    }

    private async Task<int> SeedUserAsync(
        int orgId, string email, bool siteAdmin = false, UserRole role = UserRole.User, bool withLink = false)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = orgId,
            Email = email,
            DisplayName = "Test User",
            PasswordHash = NewAuth(ctx).HashPassword(Password),
            Role = role,
            Status = UserStatus.Active,
            IsSiteAdmin = siteAdmin,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        if (withLink)
        {
            ctx.UserExternalLogins.Add(new UserExternalLogin
            {
                UserId = user.Id, Provider = "entra", Issuer = TenantA,
                Subject = Guid.NewGuid().ToString(), DisplayIdentity = email, CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }
        return user.Id;
    }

    [Fact]
    public async Task Password_login_is_refused_for_an_entra_only_org()
    {
        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.EntraOnly);
        await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        await using var ctx = _db.NewContext();

        var (outcome, user) = await NewAuth(ctx).TryLoginAsync("mette@cronus.com", Password, Ip);

        outcome.Should().Be(LoginOutcome.LocalLoginDisabled);
        user.Should().BeNull();
    }

    [Fact]
    public async Task Site_admin_password_login_survives_entra_only_as_break_glass()
    {
        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.EntraOnly);
        await SeedUserAsync(TestDb.DefaultOrgId, "operator@cronus.com", siteAdmin: true);
        await using var ctx = _db.NewContext();

        var (outcome, user) = await NewAuth(ctx).TryLoginAsync("operator@cronus.com", Password, Ip);

        outcome.Should().Be(LoginOutcome.Success);
        user.Should().NotBeNull();
    }

    [Fact]
    public async Task Magic_link_issuance_and_consumption_are_refused_for_an_entra_only_org()
    {
        await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        string preIssued;
        await using (var ctx = _db.NewContext())
        {
            // Issue while the org still allows local login...
            var resets = new PasswordResetService(ctx, NewAuth(ctx), TimeProvider.System);
            preIssued = (await resets.CreateMagicLoginTokenAsync("mette@cronus.com", Ip))!;
            preIssued.Should().NotBeNull();
        }

        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.EntraOnly);

        await using (var ctx = _db.NewContext())
        {
            var resets = new PasswordResetService(ctx, NewAuth(ctx), TimeProvider.System);
            (await resets.CreateMagicLoginTokenAsync("mette@cronus.com", Ip))
                .Should().BeNull("no new magic links for a Microsoft-only org");
            Func<Task> act = () => resets.ConsumeMagicLoginTokenAsync(preIssued);
            var ex = await act.Should().ThrowAsync<PlanValidationException>(
                "a link issued before the policy flip must not bypass it");
            ex.Which.Errors["Token"].Should().Contain("Microsoft");
        }
    }

    [Fact]
    public async Task Password_reset_tokens_are_not_issued_for_an_entra_only_org()
    {
        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.EntraOnly);
        await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        await using var ctx = _db.NewContext();
        var resets = new PasswordResetService(ctx, NewAuth(ctx), TimeProvider.System);

        (await resets.CreatePasswordResetTokenAsync("mette@cronus.com")).Should().BeNull();
    }

    [Fact]
    public async Task Entra_only_cannot_be_enabled_until_an_admin_has_a_link()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationAdminService(ctx);
        var input = new OrgEntraInput(
            Enabled: true, AllowedTenantIds: new[] { TenantA },
            ClientId: "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee", ClientSecret: null,
            ClearClientSecret: false, LocalLoginPolicy: LocalLoginPolicy.EntraOnly);

        Func<Task> act = () => svc.SaveEntraAsync(input);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("LocalLoginPolicy");

        await SeedUserAsync(TestDb.DefaultOrgId, "admin@cronus.com", role: UserRole.Admin, withLink: true);
        await svc.SaveEntraAsync(input);

        var view = await _db.NewOrganizationAdminService(_db.NewContext()).GetEntraViewAsync();
        view.LocalLoginPolicy.Should().Be(LocalLoginPolicy.EntraOnly);
    }

    [Fact]
    public async Task Entra_only_requires_entra_to_be_enabled()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationAdminService(ctx);

        Func<Task> act = () => svc.SaveEntraAsync(new OrgEntraInput(
            Enabled: false, AllowedTenantIds: Array.Empty<string>(),
            ClientId: null, ClientSecret: null, ClearClientSecret: false,
            LocalLoginPolicy: LocalLoginPolicy.EntraOnly));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("LocalLoginPolicy");
    }

    [Fact]
    public async Task Unlink_refuses_the_last_link_in_an_entra_only_org()
    {
        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.EntraOnly);
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com", withLink: true);
        await using var ctx = _db.NewContext();
        var entra = NewEntra(ctx);
        var linkId = (await entra.ListLinksAsync(userId)).Single().Id;

        Func<Task> act = () => entra.UnlinkAsync(userId, linkId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors["EntraLink"].Should().Contain("lock you out");
    }

    [Fact]
    public async Task Unlink_works_when_local_login_is_still_allowed()
    {
        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.AllowAll);
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com", withLink: true);
        await using var ctx = _db.NewContext();
        var entra = NewEntra(ctx);
        var linkId = (await entra.ListLinksAsync(userId)).Single().Id;

        await entra.UnlinkAsync(userId, linkId);

        (await entra.ListLinksAsync(userId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Link_refuses_a_tenant_not_on_the_orgs_allow_list()
    {
        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.AllowAll);
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        await using var ctx = _db.NewContext();

        Func<Task> act = () => NewEntra(ctx).LinkAsync(userId,
            new EntraTokenIdentity("99999999-9999-9999-9999-999999999999", Oid, "mette@cronus.com", "Mette"));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("EntraLink");
    }

    [Fact]
    public async Task Link_refuses_an_identity_already_connected_to_someone_else()
    {
        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.AllowAll);
        var first = await SeedUserAsync(TestDb.DefaultOrgId, "first@cronus.com");
        var second = await SeedUserAsync(TestDb.DefaultOrgId, "second@cronus.com");
        await using var ctx = _db.NewContext();
        var entra = NewEntra(ctx);
        await entra.LinkAsync(first, new EntraTokenIdentity(TenantA, Oid, "first@cronus.com", null));

        Func<Task> act = () => entra.LinkAsync(second, new EntraTokenIdentity(TenantA, Oid, "second@cronus.com", null));
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors["EntraLink"].Should().Contain("different user");

        // Re-linking your own identity is a no-op, not an error.
        await entra.LinkAsync(first, new EntraTokenIdentity(TenantA, Oid, "first@cronus.com", null));
    }

    [Fact]
    public async Task Login_surface_collapses_the_password_form_only_when_every_org_is_entra_only()
    {
        await SetPolicyAsync(TestDb.DefaultOrgId, LocalLoginPolicy.EntraOnly);
        await using (var ctx = _db.NewContext())
        {
            var (entra, passwordPrimary) = await NewEntra(ctx).GetLoginSurfaceAsync();
            entra.Should().BeTrue();
            passwordPrimary.Should().BeTrue("the Other org still allows passwords");
        }

        await SetPolicyAsync(TestDb.OtherOrgId, LocalLoginPolicy.EntraOnly, entraEnabled: false);
        await using (var ctx = _db.NewContext())
        {
            var (_, passwordPrimary) = await NewEntra(ctx).GetLoginSurfaceAsync();
            passwordPrimary.Should().BeFalse("every org is Microsoft-only now");
        }
    }
}
