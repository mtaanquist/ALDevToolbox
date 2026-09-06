using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// What a solution's repository rows keep their identity through.
///
/// <para>A pipeline that publishes to a repository points at that
/// <c>oe_project_repositories</c> row by id, and the foreign key is
/// <c>ON DELETE SET NULL</c>. Saving the solution used to drop every row and
/// re-add it, so renaming a solution silently unset the Release repository of
/// every pipeline in it - a pipeline left saying it draws from GitHub Releases
/// with no repository to draw from. These pin the reconcile that replaced it,
/// and the one case where the nulling is what the user asked for.</para>
/// </summary>
public sealed class ProjectRepositoryReconcileTests : IDisposable
{
    private const string RepoUrl = "https://github.com/cronus-dk/customer-app";
    private const string OtherUrl = "https://github.com/cronus-dk/payments";

    private readonly TestDb _db = new();

    public ProjectRepositoryReconcileTests() => _db.OrgContext.IsSiteAdmin = true;

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Renaming_a_solution_leaves_its_pipelines_release_repository_alone()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewProjectService(ctx);
        var projectId = await svc.CreateProjectAsync(NewInput("CRONUS Retail", RepoUrl, "customer-app"));
        var repositoryId = await RepositoryIdAsync(projectId);
        var (pipelineId, releasePipelineId) = await SeedPipelinesAsync(projectId, repositoryId);

        await svc.UpdateProjectAsync(projectId, NewInput("CRONUS Retail DK", RepoUrl, "customer-app"));

        await using var read = _db.NewContext();
        (await read.OeProjectRepositories.SingleAsync(r => r.ProjectId == projectId)).Id
            .Should().Be(repositoryId, "the repository is the same one, so it keeps its id");
        (await read.OePipelines.SingleAsync(p => p.Id == pipelineId)).GithubReleaseRepositoryId
            .Should().Be(repositoryId);
        (await read.OeReleasePipelines.SingleAsync(r => r.Id == releasePipelineId)).GithubReleaseRepositoryId
            .Should().Be(repositoryId);
    }

    [Fact]
    public async Task Editing_a_repositorys_display_name_keeps_the_row_and_its_pipelines()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewProjectService(ctx);
        var projectId = await svc.CreateProjectAsync(NewInput("CRONUS Retail", RepoUrl, "customer-app"));
        var repositoryId = await RepositoryIdAsync(projectId);
        var (_, releasePipelineId) = await SeedPipelinesAsync(projectId, repositoryId);

        await svc.UpdateProjectAsync(projectId, NewInput("CRONUS Retail", RepoUrl, "Customer app (DK)"));

        await using var read = _db.NewContext();
        var repository = await read.OeProjectRepositories.SingleAsync(r => r.ProjectId == projectId);
        repository.Id.Should().Be(repositoryId);
        repository.DisplayName.Should().Be("Customer app (DK)");
        (await read.OeReleasePipelines.SingleAsync(r => r.Id == releasePipelineId)).GithubReleaseRepositoryId
            .Should().Be(repositoryId);
    }

    [Fact]
    public async Task A_repository_spelled_differently_is_still_the_same_repository()
    {
        // The same repository is typed with and without the .git suffix, and in
        // either case. Two spellings must not become two rows.
        await using var ctx = _db.NewContext();
        var svc = _db.NewProjectService(ctx);
        var projectId = await svc.CreateProjectAsync(NewInput("CRONUS Retail", RepoUrl, "customer-app"));
        var repositoryId = await RepositoryIdAsync(projectId);

        await svc.UpdateProjectAsync(projectId, NewInput(
            "CRONUS Retail", "https://github.com/CRONUS-dk/Customer-App.git", "customer-app"));

        await using var read = _db.NewContext();
        var repository = await read.OeProjectRepositories.SingleAsync(r => r.ProjectId == projectId);
        repository.Id.Should().Be(repositoryId);
        repository.Url.Should().Be("https://github.com/CRONUS-dk/Customer-App.git", "the spelling the user saved is kept");
    }

    [Fact]
    public async Task Removing_a_repository_from_the_solution_still_unsets_the_pipelines_that_used_it()
    {
        // The one case where losing the Release repository is what was asked for.
        await using var ctx = _db.NewContext();
        var svc = _db.NewProjectService(ctx);
        var projectId = await svc.CreateProjectAsync(NewInput("CRONUS Retail", RepoUrl, "customer-app"));
        var repositoryId = await RepositoryIdAsync(projectId);
        var (pipelineId, releasePipelineId) = await SeedPipelinesAsync(projectId, repositoryId);

        await svc.UpdateProjectAsync(projectId, NewInput("CRONUS Retail", OtherUrl, "payments"));

        await using var read = _db.NewContext();
        (await read.OeProjectRepositories.SingleAsync(r => r.ProjectId == projectId)).Url.Should().Be(OtherUrl);
        (await read.OePipelines.SingleAsync(p => p.Id == pipelineId)).GithubReleaseRepositoryId.Should().BeNull();
        (await read.OeReleasePipelines.SingleAsync(r => r.Id == releasePipelineId)).GithubReleaseRepositoryId.Should().BeNull();
    }

    // --- Fixture -----------------------------------------------------------

    private static ProjectInput NewInput(string name, string url, string displayName) =>
        new(name, "dk", [new ProjectRepositoryInput(RepositoryProvider.GitHub, url, displayName)]);

    private async Task<int> RepositoryIdAsync(int projectId)
    {
        await using var read = _db.NewContext();
        return await read.OeProjectRepositories.Where(r => r.ProjectId == projectId).Select(r => r.Id).SingleAsync();
    }

    /// <summary>A build pipeline and a release pipeline, both naming the repository as their Release source.</summary>
    private async Task<(int PipelineId, int ReleasePipelineId)> SeedPipelinesAsync(int projectId, int repositoryId)
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var pipeline = new Pipeline
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = "Build",
            GithubReleaseRepositoryId = repositoryId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();

        var environment = new ProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = "Production",
            Type = "Production",
            FetchedAt = now,
        };
        ctx.OeProjectEnvironments.Add(environment);
        await ctx.SaveChangesAsync();

        var releasePipeline = new ReleasePipeline
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = "To production",
            BuildPipelineId = pipeline.Id,
            ProjectEnvironmentId = environment.Id,
            ArtifactSource = ReleaseArtifactSource.GithubRelease,
            GithubReleaseRepositoryId = repositoryId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.OeReleasePipelines.Add(releasePipeline);
        await ctx.SaveChangesAsync();
        return (pipeline.Id, releasePipeline.Id);
    }
}
