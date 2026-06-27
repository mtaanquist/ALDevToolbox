using System.Text.Json;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Drives <see cref="ProjectBuildImporter.StartBuildAsync"/> against the shared
/// <see cref="TestDb"/> fixture: a build is a run of a <see cref="Pipeline"/>, so the
/// pipeline's extension selection is snapshotted onto the <see cref="ProjectBuild"/>
/// row (and the build is linked to both pipeline and project). The clone/compile path
/// the build then runs is exercised by the staging smoke, not here.
/// </summary>
public sealed class ProjectBuildImporterTests : IDisposable
{
    private readonly TestDb _db = new();

    public ProjectBuildImporterTests()
    {
        // A build trigger requires owner/Admin rights; act as a SiteAdmin so the
        // access gate passes without seeding a user (StartedByUserId stays null).
        _db.OrgContext.IsSiteAdmin = true;
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task StartBuildAsync_snapshots_the_pipelines_selection_onto_the_build()
    {
        var selection = new[] { "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222" };
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", JsonSerializer.Serialize(selection));

        var releaseId = await NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);

        await using var read = _db.NewContext();
        var build = await read.OeProjectBuilds.SingleAsync(b => b.ReleaseId == releaseId);
        build.PipelineId.Should().Be(pipelineId);
        build.ProjectId.Should().Be(projectId);
        build.Status.Should().Be(ProjectBuildStatus.Queued);
        build.RequestedAppIdsJson.Should().NotBeNull();
        JsonSerializer.Deserialize<List<string>>(build.RequestedAppIdsJson!).Should().Equal(selection);
    }

    [Fact]
    public async Task StartBuildAsync_stores_null_when_the_pipeline_builds_everything()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "All", requestedAppIdsJson: null);

        var releaseId = await NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);

        await using var read = _db.NewContext();
        var build = await read.OeProjectBuilds.SingleAsync(b => b.ReleaseId == releaseId);
        build.RequestedAppIdsJson.Should().BeNull("a null pipeline selection means build every discovered extension");
    }

    [Fact]
    public async Task StartBuildAsync_rejects_a_pipeline_whose_project_has_no_repositories()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx); // no repositories
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);

        var act = () => NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);

        await act.Should().ThrowAsync<PlanValidationException>();
    }

    [Fact]
    public async Task StartBuildAsync_rejects_a_missing_pipeline()
    {
        await using var ctx = _db.NewContext();

        var act = () => NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(424242);

        await act.Should().ThrowAsync<PlanValidationException>();
    }

    private ProjectBuildImporter NewImporter(Data.AppDbContext ctx, ReleaseImportQueue queue)
    {
        var translations = new TranslationImportService(
            ctx, _db.OrgContext,
            new ALDevToolbox.Services.Translation.TranslationMemoryService(
                ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance),
            NullLogger<TranslationImportService>.Instance);
        var importer = new ReleaseImportService(
            ctx, _db.OrgContext, _db.NewQuotaGuard(ctx), translations, NullLogger<ReleaseImportService>.Instance);
        var persistedJobs = new PersistedImportJobs(ctx, TimeProvider.System);
        var access = new ProjectAccess(ctx, _db.OrgContext);
        return new ProjectBuildImporter(
            importer, queue, persistedJobs, ctx, _db.OrgContext, access,
            NullLogger<ProjectBuildImporter>.Instance);
    }

    private static async Task<int> SeedProjectAsync(Data.AppDbContext ctx)
    {
        var project = new Project
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS " + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private static async Task<int> SeedProjectWithRepoAsync(Data.AppDbContext ctx)
    {
        var projectId = await SeedProjectAsync(ctx);
        ctx.OeProjectRepositories.Add(new ProjectRepository
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Provider = RepositoryProvider.GitHub,
            Url = "https://github.com/cronus/core",
            DisplayName = "core",
        });
        await ctx.SaveChangesAsync();
        return projectId;
    }

    private static async Task<int> SeedPipelineAsync(Data.AppDbContext ctx, int projectId, string name, string? requestedAppIdsJson)
    {
        var pipeline = new Pipeline
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = name,
            RequestedAppIdsJson = requestedAppIdsJson,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();
        return pipeline.Id;
    }
}
