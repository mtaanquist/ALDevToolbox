using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.Palette;
using ALDevToolbox.Services.Palette.Sources;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Palette;

/// <summary>
/// The palette's Pipelines source (#885). A pipeline inherits its Solution's
/// visibility, so the harness is the point: a Private Solution's pipeline must
/// not come back, and neither must that Solution's name in the subtitle.
/// </summary>
public sealed class PipelinePaletteSourceTests : PaletteSourceVisibilityTestBase
{
    protected override IPaletteSource CreateSource(PaletteSourceUnderTest context) =>
        new PipelinePaletteSource(context.Db, context.Access, Db.NewToolEnablement(context.Db));

    /// <summary>One pipeline called Production per Solution the harness seeds.</summary>
    protected override Task SeedRowAsync(AppDbContext ctx, PaletteSourceSeed seed)
    {
        ctx.OePipelines.Add(NewPipeline(seed.OrganizationId, seed.ProjectId, "Production"));
        return Task.CompletedTask;
    }

    protected override IEnumerable<(string Description, ClaimsPrincipal Principal)> PrincipalsThatGetNothing()
    {
        foreach (var entry in base.PrincipalsThatGetNothing()) yield return entry;
        yield return ("a member whose organisation has switched Pipelines off",
            RecipePaletteSourceTests.MemberPrincipal(ToolKey.Pipelines));
    }

    [Fact]
    public async Task A_pipeline_is_found_by_its_own_name()
    {
        await SeedWorldAsync();
        await AddPipelineAsync("Nightly Sandbox");

        var results = await SearchAsync("nightly");

        var row = results.Should().ContainSingle().Subject;
        row.Kind.Should().Be("pipeline");
        row.Title.Should().Be("Nightly Sandbox");
        row.Href.Should().MatchRegex(@"^/pipelines/\d+$");
    }

    [Fact]
    public async Task A_pipeline_is_found_by_its_solution_and_its_name_together()
    {
        await SeedWorldAsync();

        var results = await SearchAsync("cronus prod");

        var row = results.Should().ContainSingle().Subject;
        row.Title.Should().Be("Production");
        row.Subtitle.Should().StartWith(VisibleName, "the Solution is what tells two Production pipelines apart");
    }

    [Fact]
    public async Task A_pipeline_is_found_by_its_solutions_short_name_without_claiming_the_top_hit()
    {
        await SeedWorldAsync();

        var results = await SearchAsync(VisibleShortName);

        var row = results.Should().ContainSingle().Subject;
        row.Title.Should().Be("Production");
        row.ShortName.Should().BeNull("an exact short name lifts the Solution itself, never its pipeline");
    }

    [Fact]
    public async Task A_pipeline_with_no_builds_says_so()
    {
        await SeedWorldAsync();

        var results = await SearchAsync("cronus prod");

        results.Should().ContainSingle().Which.Subtitle.Should().Be($"{VisibleName} - No builds yet");
    }

    [Fact]
    public async Task The_subtitle_reports_the_latest_build_in_the_pipelines_pages_words()
    {
        await SeedWorldAsync();
        var pipelineId = await PipelineIdAsync("Production");
        await AddBuildAsync(pipelineId, ProjectBuildStatus.Ready, DateTime.UtcNow.AddDays(-3));
        await AddBuildAsync(pipelineId, ProjectBuildStatus.Failed, DateTime.UtcNow.AddHours(-2));

        var results = await SearchAsync("cronus prod");

        results.Should().ContainSingle().Which.Subtitle.Should().Be(
            $"{VisibleName} - Failed 2 hours ago", "the newest build is the one the list reports");
    }

    [Fact]
    public async Task A_deleted_pipeline_is_left_out()
    {
        await SeedWorldAsync();
        await AddPipelineAsync("Retired Pipeline", deletedAt: DateTime.UtcNow);

        var results = await SearchAsync("retired");

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task A_deleted_solutions_pipeline_is_left_out()
    {
        await SeedWorldAsync();
        await using (var ctx = Db.NewContext())
        {
            var project = await ctx.OeProjects.SingleAsync(p => p.Id == VisibleProjectId);
            project.DeletedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        var results = await SearchAsync("production");

        results.Should().BeEmpty("a deleted Solution takes its pipelines with it");
    }

    [Fact]
    public void The_link_points_at_a_page_that_exists() =>
        RecipePaletteSourceTests.RouteTemplates().Should().Contain("/pipelines/{PipelineId:int}",
            "the palette's link is a dead end unless a page claims that route");

    [Fact]
    public async Task An_ordinary_member_passes_the_gate()
    {
        await using var ctx = Db.NewContext();
        var source = new PipelinePaletteSource(ctx, new ProjectAccess(ctx, Db.OrgContext), Db.NewToolEnablement(ctx));

        (await source.IsAvailableAsync(CallerPrincipal(), CancellationToken.None)).Should().BeTrue();
    }

    // ── Seeding ─────────────────────────────────────────────────────────

    private static OePipeline NewPipeline(int organizationId, int projectId, string name, DateTime? deletedAt = null) => new()
    {
        OrganizationId = organizationId,
        ProjectId = projectId,
        Name = name,
        DeletedAt = deletedAt,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private async Task AddPipelineAsync(string name, DateTime? deletedAt = null)
    {
        await using var ctx = Db.NewContext();
        ctx.OePipelines.Add(NewPipeline(TestDb.DefaultOrgId, VisibleProjectId, name, deletedAt));
        await ctx.SaveChangesAsync();
    }

    private async Task<int> PipelineIdAsync(string name)
    {
        await using var ctx = Db.NewContext();
        return (await ctx.OePipelines.FirstAsync(p => p.ProjectId == VisibleProjectId && p.Name == name)).Id;
    }

    private async Task AddBuildAsync(int pipelineId, string status, DateTime finishedAt)
    {
        await using var ctx = Db.NewContext();
        ctx.OeProjectBuilds.Add(new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = VisibleProjectId,
            PipelineId = pipelineId,
            Status = status,
            StartedAt = finishedAt.AddMinutes(-5),
            FinishedAt = finishedAt,
        });
        await ctx.SaveChangesAsync();
    }
}
