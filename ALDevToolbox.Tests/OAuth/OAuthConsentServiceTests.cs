using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.OAuth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.OAuth;

/// <summary>
/// The consent-only read behind the MCP setup page's "connect your assistant"
/// step: a yes/no that must stay inside the organisation query filter.
/// </summary>
public sealed class OAuthConsentServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Reports_no_consent_before_an_assistant_connects()
    {
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "member@cronus.test");

        await using var ctx = _db.NewContext();
        (await new OAuthConsentService(ctx).HasAnyConsentAsync(userId)).Should().BeFalse();
    }

    [Fact]
    public async Task Reports_a_consent_once_the_user_has_granted_one()
    {
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "member@cronus.test");
        await SeedConsentAsync(TestDb.DefaultOrgId, userId);

        await using var ctx = _db.NewContext();
        (await new OAuthConsentService(ctx).HasAnyConsentAsync(userId)).Should().BeTrue();
    }

    [Fact]
    public async Task Does_not_see_a_consent_belonging_to_another_organisation()
    {
        var strangerId = await SeedUserAsync(TestDb.OtherOrgId, "stranger@cronus.test");
        await SeedConsentAsync(TestDb.OtherOrgId, strangerId);

        // The fixture context is scoped to the Default org, so the other org's
        // consent must not be visible even when asked for by user id.
        await using var ctx = _db.NewContext();
        (await new OAuthConsentService(ctx).HasAnyConsentAsync(strangerId)).Should().BeFalse();
    }

    private async Task<int> SeedUserAsync(int orgId, string email)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = orgId,
            Email = email,
            DisplayName = email,
            PasswordHash = "placeholder",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    private async Task SeedConsentAsync(int orgId, int userId)
    {
        await using var ctx = _db.NewContext();
        ctx.OAuthConsents.Add(new OAuthConsent
        {
            OrganizationId = orgId,
            UserId = userId,
            ClientId = "client-" + Guid.NewGuid().ToString("N"),
            ScopesGranted = "mcp",
            GrantedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }
}
