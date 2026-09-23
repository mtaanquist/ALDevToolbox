using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Palette;
using ALDevToolbox.Services.Palette.Sources;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Palette;

/// <summary>
/// The palette's Teams source (#885). Teams belong to the organisation, not to a
/// Solution, so <see cref="PaletteSourceVisibilityTestBase.RowsBelongToSolutions"/>
/// is false and the Private-Solution case reports as skipped; another
/// organisation's teams and a signed-out caller are still covered by the
/// harness.
/// </summary>
public sealed class TeamPaletteSourceTests : PaletteSourceVisibilityTestBase
{
    protected override bool RowsBelongToSolutions => false;

    protected override IPaletteSource CreateSource(PaletteSourceUnderTest context) =>
        new TeamPaletteSource(context.Db, context.OrgContext);

    /// <summary>One team per Solution the harness seeds, named for it.</summary>
    protected override Task SeedRowAsync(AppDbContext ctx, PaletteSourceSeed seed)
    {
        ctx.Teams.Add(NewTeam(seed.OrganizationId, seed.Title));
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_team_is_found_by_its_name()
    {
        await SeedWorldAsync();
        await AddTeamAsync("Service Desk");

        var results = await SearchAsync("service desk");

        var row = results.Should().ContainSingle().Subject;
        row.Kind.Should().Be("team");
        row.Title.Should().Be("Service Desk");
        row.Subtitle.Should().Be("0 members");
        row.Href.Should().MatchRegex(@"^/teams/\d+$");
    }

    [Fact]
    public async Task A_team_the_caller_is_not_on_is_still_offered()
    {
        // /teams/{id} opens any team's roster for anyone signed in, so a team
        // the caller is not on is one they can open - which is the palette's
        // one rule for what it may return.
        await SeedWorldAsync();
        await AddTeamAsync("Integration Squad", memberIds: [StrangerUserId]);

        var results = await SearchAsync("integration");

        results.Should().ContainSingle().Which.Subtitle.Should().Be("1 member");
    }

    [Fact]
    public async Task A_team_the_caller_is_on_says_so()
    {
        await SeedWorldAsync();
        await AddTeamAsync("Upgrade Crew", memberIds: [CallerUserId, StrangerUserId]);

        var results = await SearchAsync("upgrade crew");

        results.Should().ContainSingle().Which.Subtitle.Should().Be("Your team - 2 members");
    }

    [Fact]
    public void The_link_points_at_a_page_that_exists() =>
        RecipePaletteSourceTests.RouteTemplates().Should().Contain("/teams/{Id:int}",
            "the palette's link is a dead end unless a page claims that route");

    [Fact]
    public async Task Any_signed_in_member_passes_the_gate()
    {
        await using var ctx = Db.NewContext();
        var source = new TeamPaletteSource(ctx, Db.OrgContext);

        (await source.IsAvailableAsync(CallerPrincipal(), CancellationToken.None)).Should().BeTrue(
            "the sidebar's Teams entry asks for nothing but a signed-in person");
    }

    // ── Seeding ─────────────────────────────────────────────────────────

    private static Team NewTeam(int organizationId, string name) => new()
    {
        OrganizationId = organizationId,
        Name = name,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private async Task AddTeamAsync(string name, int[]? memberIds = null)
    {
        await using var ctx = Db.NewContext();
        var team = NewTeam(TestDb.DefaultOrgId, name);
        foreach (var userId in memberIds ?? [])
        {
            team.Members.Add(new TeamMember { OrganizationId = TestDb.DefaultOrgId, UserId = userId, CreatedAt = DateTime.UtcNow });
        }
        ctx.Teams.Add(team);
        await ctx.SaveChangesAsync();
    }
}
