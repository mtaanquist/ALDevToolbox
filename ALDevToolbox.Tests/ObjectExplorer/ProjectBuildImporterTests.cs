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
/// <see cref="TestDb"/> fixture: the per-build extension selection the "New build"
/// dialog passes is persisted on the <see cref="ProjectBuild"/> row (so the worker —
/// and a restart-resumed job — compiles the same subset), and "build everything"
/// (null / empty pick) is stored as a null column. The clone/compile path the build
/// then runs is exercised by the staging smoke, not here.
/// </summary>
public sealed class ProjectBuildImporterTests : IDisposable
{
    private readonly TestDb _db = new();

    public ProjectBuildImporterTests()
    {
        // A build trigger requires owner/Admin rights; act as a SiteAdmin so the
        // access gate passes without seeding a user (and StartedByUserId stays null,
        // which the nullable FK allows).
        _db.OrgContext.IsSiteAdmin = true;
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task StartBuildAsync_persists_the_selected_app_ids_as_json()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var selection = new[] { "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222" };

        var releaseId = await NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(projectId, selection);

        await using var read = _db.NewContext();
        var build = await read.OeProjectBuilds.SingleAsync(b => b.ReleaseId == releaseId);
        build.RequestedAppIdsJson.Should().NotBeNull();
        JsonSerializer.Deserialize<List<string>>(build.RequestedAppIdsJson!)
            .Should().Equal(selection);
        build.Status.Should().Be(ProjectBuildStatus.Queued);
    }

    [Fact]
    public async Task StartBuildAsync_stores_null_when_building_everything()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);

        var releaseId = await NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(projectId, selectedAppIds: null);

        await using var read = _db.NewContext();
        var build = await read.OeProjectBuilds.SingleAsync(b => b.ReleaseId == releaseId);
        build.RequestedAppIdsJson.Should().BeNull("a null selection means build every discovered extension");
    }

    [Fact]
    public async Task StartBuildAsync_treats_an_empty_selection_as_build_everything()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);

        var releaseId = await NewImporter(ctx, new ReleaseImportQueue())
            .StartBuildAsync(projectId, Array.Empty<string>());

        await using var read = _db.NewContext();
        var build = await read.OeProjectBuilds.SingleAsync(b => b.ReleaseId == releaseId);
        build.RequestedAppIdsJson.Should().BeNull();
    }

    [Fact]
    public async Task StartBuildAsync_rejects_a_project_with_no_repositories()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx); // no repositories

        var act = () => NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(projectId, selectedAppIds: null);

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
}
