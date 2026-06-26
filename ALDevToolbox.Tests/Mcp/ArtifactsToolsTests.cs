using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.Mcp.Tools;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace ALDevToolbox.Tests.Mcp;

/// <summary>
/// The MCP Artifacts surface — the agent-facing parallel of the Projects/Artifacts
/// web tools. Pins project/build listing, build detail with download paths, and the
/// project-scoped guard on compare_project_builds. See .design/artifacts.md.
/// </summary>
public sealed class ArtifactsToolsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ArtifactsTools NewTools(Data.AppDbContext ctx) =>
        new(new ArtifactService(ctx),
            new ReleaseComparisonService(ctx, NullLogger<ReleaseComparisonService>.Instance),
            ctx);

    [Fact]
    public async Task List_projects_and_builds_round_trip_by_name_and_id()
    {
        int projectId;
        await using (var ctx = _db.NewContext())
        {
            projectId = await SeedProjectAsync(ctx, "CRONUS A/S");
            await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, DateTime.UtcNow, bcVersion: "26.0", artifacts: 2);
        }

        await using var read = _db.NewContext();
        var tools = NewTools(read);

        (await tools.ListProjectsAsync()).Should().ContainSingle(p => p.Name == "CRONUS A/S");
        (await tools.ListProjectBuildsAsync("CRONUS A/S")).Should().ContainSingle();
        (await tools.ListProjectBuildsAsync(projectId.ToString())).Should().ContainSingle();
    }

    [Fact]
    public async Task Get_project_build_returns_download_paths()
    {
        int buildId;
        await using (var ctx = _db.NewContext())
        {
            var projectId = await SeedProjectAsync(ctx, "CRONUS A/S");
            buildId = await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, DateTime.UtcNow, bcVersion: "26.0", artifacts: 1);
            ctx.OeProjectBuildLogs.Add(new ProjectBuildLog
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = buildId,
                Section = "Build", Content = "ok", Ordering = 0, CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var detail = await NewTools(read).GetProjectBuildAsync(buildId);

        detail.Apps.Should().ContainSingle();
        detail.Apps[0].DownloadPath.Should().StartWith($"/artifacts/build/{buildId}/app/");
        detail.DownloadAllPath.Should().Be($"/artifacts/build/{buildId}/all");
        detail.RawLogPath.Should().Be($"/artifacts/build/{buildId}/log");
    }

    [Fact]
    public async Task Get_project_build_throws_for_a_missing_build()
    {
        await using var read = _db.NewContext();
        var act = () => NewTools(read).GetProjectBuildAsync(999999);
        await act.Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task Compare_project_builds_rejects_builds_from_different_projects()
    {
        int b1, b2;
        await using (var ctx = _db.NewContext())
        {
            var p1 = await SeedProjectAsync(ctx, "Project One");
            var p2 = await SeedProjectAsync(ctx, "Project Two");
            b1 = await SeedBuildAsync(ctx, p1, ProjectBuildStatus.Ready, DateTime.UtcNow, releaseId: await SeedReleaseAsync(ctx));
            b2 = await SeedBuildAsync(ctx, p2, ProjectBuildStatus.Ready, DateTime.UtcNow, releaseId: await SeedReleaseAsync(ctx));
        }

        await using var read = _db.NewContext();
        var act = () => NewTools(read).CompareProjectBuildsAsync(b1, b2);
        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("same project");
    }

    [Fact]
    public async Task Compare_project_builds_rejects_a_non_ready_build()
    {
        int b1, b2;
        await using (var ctx = _db.NewContext())
        {
            var p = await SeedProjectAsync(ctx, "CRONUS A/S");
            b1 = await SeedBuildAsync(ctx, p, ProjectBuildStatus.Ready, DateTime.UtcNow, releaseId: await SeedReleaseAsync(ctx));
            b2 = await SeedBuildAsync(ctx, p, ProjectBuildStatus.Failed, DateTime.UtcNow);
        }

        await using var read = _db.NewContext();
        var act = () => NewTools(read).CompareProjectBuildsAsync(b1, b2);
        await act.Should().ThrowAsync<McpException>();
    }

    // ── seeding ─────────────────────────────────────────────────────────

    private static async Task<int> SeedProjectAsync(Data.AppDbContext ctx, string name)
    {
        var project = new Project
        {
            OrganizationId = TestDb.DefaultOrgId, Name = name, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private static async Task<int> SeedReleaseAsync(Data.AppDbContext ctx)
    {
        var release = new Release
        {
            OrganizationId = TestDb.DefaultOrgId, Label = "build", Kind = "project", Status = "ready",
            ImportedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();
        return release.Id;
    }

    private static async Task<int> SeedBuildAsync(
        Data.AppDbContext ctx, int projectId, string status, DateTime startedAt,
        string? bcVersion = null, int artifacts = 0, int? releaseId = null)
    {
        var build = new ProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Status = status, BcVersion = bcVersion,
            StartedAt = startedAt, ReleaseId = releaseId,
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();
        for (var i = 0; i < artifacts; i++)
        {
            ctx.OeProjectBuildArtifacts.Add(new ProjectBuildArtifact
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = build.Id,
                FileName = $"app{i}.app", AppName = $"App {i}", AppVersion = "1.0.0.0",
                SizeBytes = 1, Content = new byte[] { (byte)i }, CreatedAt = DateTime.UtcNow,
            });
        }
        if (artifacts > 0) await ctx.SaveChangesAsync();
        return build.Id;
    }
}
