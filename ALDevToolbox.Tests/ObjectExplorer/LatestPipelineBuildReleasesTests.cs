using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Pins the one sanctioned leak of project builds into the user-facing Object
/// Explorer: <see cref="ObjectExplorerService.ListLatestPipelineBuildReleasesAsync"/>
/// returns exactly one row per pipeline — its newest <c>ready</c> build's
/// release — for the Third-party tab. Everything else about project builds
/// stays Artifacts-only (see the companion exclusion tests in
/// <see cref="ObjectExplorerServiceTests"/>). Also pins that the plain release
/// list carries the new <c>cal</c> kind.
/// </summary>
public sealed class LatestPipelineBuildReleasesTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ObjectExplorerService NewQuery(AppDbContext ctx) =>
        new(ctx, new ReferenceQueryService(ctx, new ProjectAccess(ctx, _db.OrgContext), _db.OrgContext, NullLogger<ReferenceQueryService>.Instance),
            new ProjectAccess(ctx, _db.OrgContext),
            NullLogger<ObjectExplorerService>.Instance);

    [Fact]
    public async Task Returns_the_newest_ready_build_per_pipeline_with_project_and_pipeline_names()
    {
        int newestReadyRelease, otherPipelineRelease;
        await using (var seed = _db.NewContext())
        {
            var projectId = await SeedProjectAsync(seed, "CRONUS");
            var pipelineA = await SeedPipelineAsync(seed, projectId, "Production");
            var pipelineB = await SeedPipelineAsync(seed, projectId, "Test");

            // Pipeline A: an older ready build, the newest ready build, and an
            // even newer build that failed — the newest *ready* one must win.
            var oldRelease = await SeedProjectReleaseAsync(seed, "CRONUS on BC 25.0");
            await SeedBuildAsync(seed, projectId, pipelineA, oldRelease,
                ProjectBuildStatus.Ready, DateTime.UtcNow.AddDays(-2));
            newestReadyRelease = await SeedProjectReleaseAsync(seed, "CRONUS on BC 26.0");
            await SeedBuildAsync(seed, projectId, pipelineA, newestReadyRelease,
                ProjectBuildStatus.Ready, DateTime.UtcNow.AddDays(-1));
            var failedRelease = await SeedProjectReleaseAsync(seed, "CRONUS on BC 26.1", status: "failed");
            await SeedBuildAsync(seed, projectId, pipelineA, failedRelease,
                ProjectBuildStatus.Failed, DateTime.UtcNow);

            otherPipelineRelease = await SeedProjectReleaseAsync(seed, "CRONUS Test on BC 26.0");
            await SeedBuildAsync(seed, projectId, pipelineB, otherPipelineRelease,
                ProjectBuildStatus.Ready, DateTime.UtcNow);
        }

        await using var read = _db.NewContext();
        var rows = await NewQuery(read).ListLatestPipelineBuildReleasesAsync();

        rows.Should().HaveCount(2, "one entry per pipeline, never per build");
        rows.Select(r => r.Id).Should().BeEquivalentTo(new[] { newestReadyRelease, otherPipelineRelease });
        var production = rows.Single(r => r.Id == newestReadyRelease);
        production.PipelineName.Should().Be("Production");
        production.ProjectName.Should().Be("CRONUS");
        production.Kind.Should().Be("project");
    }

    [Fact]
    public async Task Skips_builds_without_a_pipeline_and_releases_that_are_deleted_or_not_ready()
    {
        await using (var seed = _db.NewContext())
        {
            var projectId = await SeedProjectAsync(seed, "CRONUS");
            var pipeline = await SeedPipelineAsync(seed, projectId, "Production");

            // Legacy build with no pipeline link — invisible here.
            var orphanRelease = await SeedProjectReleaseAsync(seed, "CRONUS legacy");
            await SeedBuildAsync(seed, projectId, pipelineId: null, orphanRelease,
                ProjectBuildStatus.Ready, DateTime.UtcNow);

            // The pipeline's only ready build points at a soft-deleted release.
            var deletedRelease = await SeedProjectReleaseAsync(seed, "CRONUS deleted", deleted: true);
            await SeedBuildAsync(seed, projectId, pipeline, deletedRelease,
                ProjectBuildStatus.Ready, DateTime.UtcNow.AddHours(-1));

            // And a newer build that's still ingesting.
            var ingestingRelease = await SeedProjectReleaseAsync(seed, "CRONUS building", status: "ingesting");
            await SeedBuildAsync(seed, projectId, pipeline, ingestingRelease,
                ProjectBuildStatus.Building, DateTime.UtcNow);
        }

        await using var read = _db.NewContext();
        var rows = await NewQuery(read).ListLatestPipelineBuildReleasesAsync();
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ListReleasesAsync_includes_cal_releases_and_still_excludes_project_ones()
    {
        await using (var seed = _db.NewContext())
        {
            seed.OeReleases.Add(new Release
            {
                OrganizationId = TestDb.DefaultOrgId,
                Label = "CRONUS objects 2016",
                Kind = "cal",
                Status = "ready",
                ImportedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
            await SeedProjectReleaseAsync(seed, "CRONUS on BC 26.0");
        }

        await using var read = _db.NewContext();
        var rows = await NewQuery(read).ListReleasesAsync();
        rows.Should().ContainSingle(r => r.Kind == "cal")
            .Which.Label.Should().Be("CRONUS objects 2016");
        rows.Should().NotContain(r => r.Kind == "project");
    }

    // ── Seed helpers ─────────────────────────────────────────────────────

    private static async Task<int> SeedProjectAsync(AppDbContext ctx, string name)
    {
        var project = new Project
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private static async Task<int> SeedPipelineAsync(AppDbContext ctx, int projectId, string name)
    {
        var pipeline = new Pipeline
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();
        return pipeline.Id;
    }

    private static async Task<int> SeedProjectReleaseAsync(
        AppDbContext ctx, string label, string status = "ready", bool deleted = false)
    {
        var release = new Release
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = label,
            Kind = "project",
            Status = status,
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            DeletedAt = deleted ? DateTime.UtcNow : null,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();
        return release.Id;
    }

    private static async Task SeedBuildAsync(
        AppDbContext ctx, int projectId, int? pipelineId, int releaseId, string status, DateTime startedAt)
    {
        ctx.OeProjectBuilds.Add(new ProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            PipelineId = pipelineId,
            ReleaseId = releaseId,
            Status = status,
            StartedAt = startedAt,
        });
        await ctx.SaveChangesAsync();
    }
}
