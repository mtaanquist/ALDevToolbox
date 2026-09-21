using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.Palette;
using ALDevToolbox.Services.Palette.Sources;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Palette;

/// <summary>
/// The palette's Environments source (#882). An environment has no visibility of
/// its own, so the harness matters twice over here: an environment of a Private
/// solution the caller is not on must not come back, and neither must its
/// customer's name in the subtitle.
/// </summary>
public sealed class EnvironmentPaletteSourceTests : PaletteSourceVisibilityTestBase
{
    protected override IPaletteSource CreateSource(PaletteSourceUnderTest context) =>
        new EnvironmentPaletteSource(context.Db, context.Access, Db.NewToolEnablement(context.Db));

    /// <summary>One running Production environment per solution the harness seeds.</summary>
    protected override Task SeedRowAsync(AppDbContext ctx, PaletteSourceSeed seed)
    {
        ctx.OeProjectEnvironments.Add(NewEnvironment(seed.OrganizationId, seed.ProjectId, "Production"));
        return Task.CompletedTask;
    }

    /// <summary>
    /// The anonymous caller the harness always tries, plus one whose organisation
    /// has switched Solutions off - the gate <c>/environments</c> sits behind in
    /// the sidebar, the same one <c>/solutions</c> has.
    /// </summary>
    protected override IEnumerable<(string Description, ClaimsPrincipal Principal)> PrincipalsThatGetNothing()
    {
        foreach (var entry in base.PrincipalsThatGetNothing()) yield return entry;

        var identity = new ClaimsIdentity(
            [
                new Claim(HttpOrganizationContext.UserIdClaim, CallerUserId.ToString()),
                new Claim(HttpOrganizationContext.OrganizationIdClaim, TestDb.DefaultOrgId.ToString()),
                new Claim(ClaimTypes.Role, nameof(UserRole.User)),
                new Claim(ALDevToolbox.Endpoints.EndpointHelpers.DisabledToolsClaim, nameof(ToolKey.Projects)),
            ],
            authenticationType: "Test");
        yield return ("a caller whose organisation has switched Solutions off", new ClaimsPrincipal(identity));
    }

    [Fact]
    public async Task An_environment_leads_its_subtitle_with_the_solution_then_type_and_version()
    {
        await SeedWorldAsync();

        var results = await SearchAsync("Production");

        var row = results.Should().ContainSingle(
            "the other two solutions' environments belong to customers this caller cannot reach").Subject;
        row.Kind.Should().Be("environment");
        row.Title.Should().Be("Production");
        row.Href.Should().Be($"/environments/{await EnvironmentIdAsync("Production")}");
        row.Subtitle.Should().Be($"{VisibleName} - Production - BC version 28.2.41125.0");
    }

    [Fact]
    public async Task The_customer_and_the_environment_together_find_it()
    {
        await SeedWorldAsync();
        // "Preproduction" contains "prod" too, but not at the start of a word -
        // so this proves the order, not merely that one row came back.
        await AddEnvironmentAsync(VisibleProjectId, "Preproduction", type: "Sandbox");

        var results = await SearchAsync("cronus prod");

        results.Should().HaveCountGreaterThanOrEqualTo(2);
        results[0].Title.Should().Be("Production",
            "a term matching at the start of a word beats the same term buried in one");
    }

    [Fact]
    public async Task The_solutions_short_name_finds_its_environments_without_being_shown()
    {
        await SeedWorldAsync();

        var results = await SearchAsync($"{VisibleShortName} production");

        var row = results.Should().ContainSingle().Subject;
        row.Title.Should().Be("Production");
        row.ShortName.Should().BeNull("an environment is not abbreviated, its customer is");
        row.Subtitle.Should().NotContain(VisibleShortName,
            "the short name is searched, not printed - the subtitle is the customer's full name");
    }

    [Fact]
    public async Task A_state_worth_knowing_about_is_on_the_row_and_a_running_one_is_not()
    {
        await SeedWorldAsync();
        await AddEnvironmentAsync(VisibleProjectId, "Trial", type: "Sandbox", status: "Suspended");

        var results = await SearchAsync("Trial");

        results.Should().ContainSingle()
            .Which.Subtitle.Should().EndWith("Suspended by Microsoft",
                "the Environments list's own word for the state, and only when it is not plainly running");
    }

    [Fact]
    public async Task An_environment_the_customer_has_deleted_is_left_out()
    {
        await SeedWorldAsync();
        await AddEnvironmentAsync(
            VisibleProjectId, "Retired", type: "Sandbox", status: BcEnvironmentStatus.SoftDeleted);

        var results = await SearchAsync("Retired");

        results.Should().BeEmpty(
            "a deleted environment has its own view on the Environments page; the palette is for going somewhere");
    }

    [Fact]
    public async Task An_environment_business_central_no_longer_reports_is_left_out()
    {
        await SeedWorldAsync();
        await AddEnvironmentAsync(VisibleProjectId, "Vanished", type: "Sandbox", missing: true);

        var results = await SearchAsync("Vanished");

        results.Should().BeEmpty("its own page answers not-found, so the row would be a dead end");
    }

    // ── Plumbing ────────────────────────────────────────────────────────

    private static OeProjectEnvironment NewEnvironment(
        int organizationId,
        int projectId,
        string name,
        string type = "Production",
        string? status = BcEnvironmentStatus.Active,
        bool missing = false) => new()
    {
        OrganizationId = organizationId,
        ProjectId = projectId,
        Name = name,
        Type = type,
        Status = status,
        Version = "28.2.41125.0",
        FetchedAt = DateTime.UtcNow,
        MissingSince = missing ? DateTime.UtcNow : null,
        SoftDeletedOn = BcEnvironmentStatus.IsSoftDeleted(status) ? DateTime.UtcNow : null,
    };

    private async Task AddEnvironmentAsync(
        int projectId,
        string name,
        string type = "Production",
        string? status = BcEnvironmentStatus.Active,
        bool missing = false)
    {
        await using var ctx = Db.NewContext();
        ctx.OeProjectEnvironments.Add(
            NewEnvironment(TestDb.DefaultOrgId, projectId, name, type, status, missing));
        await ctx.SaveChangesAsync();
    }

    private async Task<int> EnvironmentIdAsync(string name)
    {
        await using var ctx = Db.NewContext();
        return (await ctx.OeProjectEnvironments
            .FirstAsync(e => e.ProjectId == VisibleProjectId && e.Name == name)).Id;
    }
}
