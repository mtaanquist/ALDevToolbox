using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.OAuth;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Abstractions;

namespace ALDevToolbox.Tests.OAuth;

/// <summary>
/// <see cref="OAuthClientAdminService.RevokeConsentAsync"/> reads the consent
/// table with <c>IgnoreQueryFilters()</c> and re-imposes scope by hand in
/// three modes — self-service, org admin, SiteAdmin — refusing silently in the
/// first two. A broken guard means a user revoking someone else's consent just
/// works, with nothing but a log line to show for it. See issue #667.
/// </summary>
public sealed class OAuthClientAdminServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
    private readonly ServiceProvider _openIddict;

    public OAuthClientAdminServiceTests()
    {
        var db = _db;
        _openIddict = new ServiceCollection()
            .AddLogging()
            .AddScoped(_ => db.NewContext())
            .AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<AppDbContext>())
            .Services
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _openIddict.Dispose();
        _db.Dispose();
    }

    private OAuthClientAdminService NewService(AppDbContext ctx) =>
        new(_openIddict.GetRequiredService<IOpenIddictApplicationManager>(),
            _openIddict.GetRequiredService<IOpenIddictTokenManager>(),
            ctx,
            _clock,
            NullLogger<OAuthClientAdminService>.Instance);

    private async Task<User> SeedUserAsync(int organizationId, string email)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = organizationId,
            Email = email,
            DisplayName = email,
            PasswordHash = "ignored",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user;
    }

    private async Task<OAuthConsent> SeedConsentAsync(User user, string clientId = "claude-desktop")
    {
        await using var ctx = _db.NewContext();
        var consent = new OAuthConsent
        {
            UserId = user.Id,
            OrganizationId = user.OrganizationId,
            ClientId = clientId,
            ScopesGranted = "mcp",
            GrantedAt = _clock.GetUtcNow().UtcDateTime,
        };
        ctx.OAuthConsents.Add(consent);
        await ctx.SaveChangesAsync();
        return consent;
    }

    private async Task<DateTime?> RevokedAtAsync(int consentId)
    {
        await using var ctx = _db.NewContext();
        return (await ctx.OAuthConsents.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(c => c.Id == consentId)).RevokedAt;
    }

    [Fact]
    public async Task Self_service_revoke_of_your_own_consent_stamps_revoked_at()
    {
        var user = await SeedUserAsync(TestDb.DefaultOrgId, "alice@cronus.test");
        var consent = await SeedConsentAsync(user);

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RevokeConsentAsync(consent.Id, actorUserId: user.Id);
        }

        (await RevokedAtAsync(consent.Id)).Should().Be(_clock.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task Self_service_revoke_of_someone_elses_consent_is_refused()
    {
        var owner = await SeedUserAsync(TestDb.DefaultOrgId, "alice@cronus.test");
        var attacker = await SeedUserAsync(TestDb.DefaultOrgId, "mallory@cronus.test");
        var consent = await SeedConsentAsync(owner);

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RevokeConsentAsync(consent.Id, actorUserId: attacker.Id);
        }

        (await RevokedAtAsync(consent.Id)).Should().BeNull("the refusal is silent, so the row is the only evidence");
    }

    [Fact]
    public async Task Org_admin_revokes_a_consent_inside_their_own_org()
    {
        var member = await SeedUserAsync(TestDb.DefaultOrgId, "alice@cronus.test");
        var admin = await SeedUserAsync(TestDb.DefaultOrgId, "admin@cronus.test");
        var consent = await SeedConsentAsync(member);

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RevokeConsentAsync(
                consent.Id, actorUserId: admin.Id, ignoreOrgScope: true, expectedOrganizationId: TestDb.DefaultOrgId);
        }

        (await RevokedAtAsync(consent.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Org_admin_cannot_revoke_a_consent_in_another_org()
    {
        var stranger = await SeedUserAsync(TestDb.OtherOrgId, "bob@other.test");
        var admin = await SeedUserAsync(TestDb.DefaultOrgId, "admin@cronus.test");
        var consent = await SeedConsentAsync(stranger);

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RevokeConsentAsync(
                consent.Id, actorUserId: admin.Id, ignoreOrgScope: true, expectedOrganizationId: TestDb.DefaultOrgId);
        }

        (await RevokedAtAsync(consent.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Site_admin_mode_revokes_a_consent_in_any_org()
    {
        var stranger = await SeedUserAsync(TestDb.OtherOrgId, "bob@other.test");
        var siteAdmin = await SeedUserAsync(TestDb.DefaultOrgId, "root@cronus.test");
        var consent = await SeedConsentAsync(stranger);

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RevokeConsentAsync(
                consent.Id, actorUserId: siteAdmin.Id, ignoreOrgScope: true, expectedOrganizationId: null);
        }

        (await RevokedAtAsync(consent.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Re_revoking_an_already_revoked_consent_is_a_no_op()
    {
        var user = await SeedUserAsync(TestDb.DefaultOrgId, "alice@cronus.test");
        var consent = await SeedConsentAsync(user);
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RevokeConsentAsync(consent.Id, actorUserId: user.Id);
        }
        var firstStamp = await RevokedAtAsync(consent.Id);

        _clock.Advance(TimeSpan.FromHours(3));
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RevokeConsentAsync(consent.Id, actorUserId: user.Id);
        }

        (await RevokedAtAsync(consent.Id)).Should().Be(firstStamp, "the original revocation time is the audit record");
    }

    [Fact]
    public async Task Revoking_an_unknown_consent_id_is_a_no_op()
    {
        var user = await SeedUserAsync(TestDb.DefaultOrgId, "alice@cronus.test");

        await using var ctx = _db.NewContext();
        var act = async () => await NewService(ctx).RevokeConsentAsync(999999, actorUserId: user.Id);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task List_for_user_returns_only_that_users_consents()
    {
        var alice = await SeedUserAsync(TestDb.DefaultOrgId, "alice@cronus.test");
        var bob = await SeedUserAsync(TestDb.DefaultOrgId, "bob@cronus.test");
        var aliceConsent = await SeedConsentAsync(alice);
        await SeedConsentAsync(bob, "cursor");

        await using var ctx = _db.NewContext();
        var rows = await NewService(ctx).ListForUserAsync(alice.Id);

        rows.Should().ContainSingle().Which.ConsentId.Should().Be(aliceConsent.Id);
        // No OAuth application is registered for the client id, so the join
        // falls back to the placeholder rather than dropping the row.
        rows[0].DisplayName.Should().Be("Deleted client");
    }

    [Fact]
    public async Task List_for_organization_returns_only_that_orgs_consents()
    {
        var alice = await SeedUserAsync(TestDb.DefaultOrgId, "alice@cronus.test");
        var stranger = await SeedUserAsync(TestDb.OtherOrgId, "bob@other.test");
        var aliceConsent = await SeedConsentAsync(alice);
        await SeedConsentAsync(stranger, "cursor");

        await using var ctx = _db.NewContext();
        var rows = await NewService(ctx).ListForOrganizationAsync(TestDb.DefaultOrgId);

        rows.Should().ContainSingle().Which.ConsentId.Should().Be(aliceConsent.Id);
        rows[0].UserEmail.Should().Be("alice@cronus.test");
    }

    [Fact]
    public async Task List_all_spans_every_org()
    {
        var alice = await SeedUserAsync(TestDb.DefaultOrgId, "alice@cronus.test");
        var stranger = await SeedUserAsync(TestDb.OtherOrgId, "bob@other.test");
        await SeedConsentAsync(alice);
        await SeedConsentAsync(stranger, "cursor");

        await using var ctx = _db.NewContext();
        var rows = await NewService(ctx).ListAllAsync();

        rows.Select(r => r.OrganizationId).Should().BeEquivalentTo(new[] { TestDb.DefaultOrgId, TestDb.OtherOrgId });
    }
}
