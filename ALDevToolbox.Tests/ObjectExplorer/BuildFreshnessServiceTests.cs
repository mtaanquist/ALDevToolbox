using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// <see cref="BuildFreshnessService.GetAsync"/> (#963): the head a push stored for
/// the watched branch, against the commit the pipeline's last successful build
/// pinned, per repository - every state, and the "what changed" lists for Ahead.
/// </summary>
public sealed class BuildFreshnessServiceTests : IDisposable
{
    private const string Built = "1111111111111111111111111111111111111111";
    private const string Newer = "2222222222222222222222222222222222222222";
    private const string Newest = "3333333333333333333333333333333333333333";

    private static readonly DateTime BuiltCommittedAt = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);

    private readonly TestDb _db = new();

    public BuildFreshnessServiceTests()
    {
        // Viewing is gated on the solution's visibility; act as SiteAdmin so a
        // Public solution needs no seeded user. The one gating test turns it off.
        _db.OrgContext.IsSiteAdmin = true;
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_branch_that_still_points_at_the_built_commit_is_up_to_date()
    {
        var s = await SeedAsync(branch: "main");
        await AddBuildAsync(s, Built);
        await AddHeadAsync(s.RepositoryId, "main", Built);

        var repo = Single(await GetAsync(s.PipelineId));

        repo.State.Should().Be(BuildFreshnessState.UpToDate);
        repo.Branch.Should().Be("main");
        repo.HeadSha.Should().Be(Built);
        repo.BuiltSha.Should().Be(Built);
        repo.MergedPullRequests.Should().BeEmpty();
        repo.Commits.Should().BeEmpty();
    }

    [Fact]
    public async Task A_branch_that_moved_is_ahead_with_what_merged_and_the_commits_since()
    {
        var s = await SeedAsync(branch: "main");
        await AddBuildAsync(s, Built);
        await AddHeadAsync(s.RepositoryId, "main", Newest, commits: [Built, Newer, Newest]);
        await AddMergedAsync(s.RepositoryId, 11, "main", BuiltCommittedAt.AddHours(-1), mergeSha: "");     // before the build
        await AddMergedAsync(s.RepositoryId, 12, "main", BuiltCommittedAt.AddHours(1), mergeSha: Newer);
        await AddMergedAsync(s.RepositoryId, 13, "main", BuiltCommittedAt.AddHours(2), mergeSha: Newest);
        await AddMergedAsync(s.RepositoryId, 14, "develop", BuiltCommittedAt.AddHours(3), mergeSha: "");  // another branch

        var repo = Single(await GetAsync(s.PipelineId));

        repo.State.Should().Be(BuildFreshnessState.Ahead);
        repo.MergedPullRequests.Select(m => m.Number).Should().Equal([13, 12], "newest first, only this branch, only since the build");
        repo.Commits.Select(c => c.Sha).Should().Equal([Newest, Newer], "the commits after the built one, newest first");
        repo.CommitsComplete.Should().BeTrue("the built commit is in the stored list, so nothing in between is missing");
    }

    [Fact]
    public async Task When_the_built_commit_is_not_among_the_stored_ones_the_commit_list_says_it_is_partial()
    {
        var s = await SeedAsync(branch: "main");
        await AddBuildAsync(s, Built);
        await AddHeadAsync(s.RepositoryId, "main", Newest, commits: [Newer, Newest], forced: true);

        var repo = Single(await GetAsync(s.PipelineId));

        repo.State.Should().Be(BuildFreshnessState.Ahead);
        repo.Forced.Should().BeTrue();
        repo.Commits.Select(c => c.Sha).Should().Equal(Newest, Newer);
        repo.CommitsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task A_pipeline_with_no_successful_build_is_never_built()
    {
        var s = await SeedAsync(branch: "main");
        await AddBuildAsync(s, Built, status: ProjectBuildStatus.Failed);
        await AddHeadAsync(s.RepositoryId, "main", Newest);

        var freshness = await GetAsync(s.PipelineId);

        Single(freshness).State.Should().Be(BuildFreshnessState.NeverBuilt);
        freshness.LastBuildId.Should().BeNull("a failed build is not the last successful one");
        Single(freshness).HeadSha.Should().Be(Newest, "the head is still reported");
    }

    [Fact]
    public async Task Another_pipelines_build_does_not_count()
    {
        var s = await SeedAsync(branch: "main");
        var other = await AddPipelineAsync(s.ProjectId, "Test", "main");
        await AddBuildAsync(s with { PipelineId = other }, Built);
        await AddHeadAsync(s.RepositoryId, "main", Built);

        Single(await GetAsync(s.PipelineId)).State.Should().Be(BuildFreshnessState.NeverBuilt);
    }

    [Fact]
    public async Task A_deleted_watched_branch_is_gone()
    {
        var s = await SeedAsync(branch: "release/25.0");
        await AddBuildAsync(s, Built);
        await AddHeadAsync(s.RepositoryId, "release/25.0", Built, deletedAt: DateTime.UtcNow);

        Single(await GetAsync(s.PipelineId)).State.Should().Be(BuildFreshnessState.BranchGone);
    }

    [Fact]
    public async Task With_no_head_stored_the_state_is_unknown()
    {
        var s = await SeedAsync(branch: "main");
        await AddBuildAsync(s, Built);
        // A head for another branch says nothing about this one.
        await AddHeadAsync(s.RepositoryId, "develop", Newest);

        var repo = Single(await GetAsync(s.PipelineId));

        repo.State.Should().Be(BuildFreshnessState.Unknown);
        repo.HeadSha.Should().BeNull();
        repo.BuiltSha.Should().Be(Built);
    }

    [Fact]
    public async Task A_pipeline_without_a_branch_watches_the_default_branch_the_pushes_reported()
    {
        var s = await SeedAsync(branch: null);
        await AddBuildAsync(s, Built);
        await AddHeadAsync(s.RepositoryId, "feature/vat", Newest);
        await AddHeadAsync(s.RepositoryId, "main", Newer, isDefault: true);

        var freshness = await GetAsync(s.PipelineId);

        freshness.Branch.Should().BeNull();
        var repo = Single(freshness);
        repo.Branch.Should().Be("main");
        repo.State.Should().Be(BuildFreshnessState.Ahead);
        repo.HeadSha.Should().Be(Newer);
    }

    [Fact]
    public async Task A_pipeline_without_a_branch_and_no_default_known_is_unknown()
    {
        var s = await SeedAsync(branch: null);
        await AddBuildAsync(s, Built);
        await AddHeadAsync(s.RepositoryId, "feature/vat", Newest);

        var repo = Single(await GetAsync(s.PipelineId));

        repo.State.Should().Be(BuildFreshnessState.Unknown);
        repo.Branch.Should().BeNull();
    }

    [Fact]
    public async Task Each_repository_of_the_solution_is_judged_on_its_own()
    {
        var s = await SeedAsync(branch: "main");
        var second = await AddRepositoryAsync(s.ProjectId, "second-app");
        await AddBuildAsync(s, Built);   // pins only the first repository
        await AddHeadAsync(s.RepositoryId, "main", Built);
        await AddHeadAsync(second, "main", Newest);

        var freshness = await GetAsync(s.PipelineId);

        freshness.Repositories.Should().HaveCount(2);
        freshness.Repositories.Single(r => r.RepositoryId == s.RepositoryId).State.Should().Be(BuildFreshnessState.UpToDate);
        freshness.Repositories.Single(r => r.RepositoryId == second).State.Should().Be(BuildFreshnessState.NeverBuilt,
            "the last build never pinned a commit in a repository added after it");
    }

    [Fact]
    public async Task The_latest_successful_build_is_the_one_compared()
    {
        var s = await SeedAsync(branch: "main");
        await AddBuildAsync(s, Built, startedAt: DateTime.UtcNow.AddDays(-2));
        var latest = await AddBuildAsync(s, Newer, startedAt: DateTime.UtcNow.AddDays(-1));
        await AddHeadAsync(s.RepositoryId, "main", Newer);

        var freshness = await GetAsync(s.PipelineId);

        freshness.LastBuildId.Should().Be(latest);
        Single(freshness).State.Should().Be(BuildFreshnessState.UpToDate);
    }

    [Fact]
    public async Task A_pipeline_that_does_not_exist_answers_null()
    {
        await using var ctx = _db.NewContext();
        (await NewService(ctx).GetAsync(424242)).Should().BeNull();
    }

    [Fact]
    public async Task A_private_solution_the_caller_cannot_see_is_refused()
    {
        var s = await SeedAsync(branch: "main", visibility: ProjectVisibility.Private);
        _db.OrgContext.IsSiteAdmin = false;

        await using var ctx = _db.NewContext();
        var act = () => NewService(ctx).GetAsync(s.PipelineId);

        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
    }

    // --- Fixture -----------------------------------------------------------

    private sealed record Seeded(int ProjectId, int RepositoryId, int PipelineId);

    private BuildFreshnessService NewService(AppDbContext ctx) =>
        new(ctx, new ProjectAccess(ctx, _db.OrgContext));

    private async Task<PipelineFreshness> GetAsync(int pipelineId)
    {
        await using var ctx = _db.NewContext();
        var freshness = await NewService(ctx).GetAsync(pipelineId);
        freshness.Should().NotBeNull();
        return freshness!;
    }

    private static RepositoryFreshness Single(PipelineFreshness freshness) =>
        freshness.Repositories.Should().ContainSingle().Subject;

    private async Task<Seeded> SeedAsync(string? branch, ProjectVisibility visibility = ProjectVisibility.Public)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS " + Guid.NewGuid().ToString("N")[..8],
            Visibility = visibility,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        var repositoryId = await AddRepositoryAsync(project.Id, "customer-app");
        var pipelineId = await AddPipelineAsync(project.Id, "Production", branch);
        return new Seeded(project.Id, repositoryId, pipelineId);
    }

    private async Task<int> AddRepositoryAsync(int projectId, string name)
    {
        await using var ctx = _db.NewContext();
        var repository = new OeProjectRepository
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Provider = RepositoryProvider.GitHub,
            Url = $"https://github.com/cronus-dk/{name}.git",
            DisplayName = name,
        };
        ctx.OeProjectRepositories.Add(repository);
        await ctx.SaveChangesAsync();
        return repository.Id;
    }

    private async Task<int> AddPipelineAsync(int projectId, string name, string? branch)
    {
        await using var ctx = _db.NewContext();
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = name,
            Branch = branch,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();
        return pipeline.Id;
    }

    private async Task<int> AddBuildAsync(
        Seeded s, string sha, string status = ProjectBuildStatus.Ready, DateTime? startedAt = null)
    {
        await using var ctx = _db.NewContext();
        var started = startedAt ?? DateTime.UtcNow.AddHours(-1);
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = s.ProjectId,
            PipelineId = s.PipelineId,
            Status = status,
            StartedAt = started,
            FinishedAt = started.AddMinutes(5),
        };
        build.RepoCommits.Add(new OeProjectBuildRepoCommit
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectRepositoryId = s.RepositoryId,
            RepoUrl = "https://github.com/cronus-dk/customer-app.git",
            RepoDisplayName = "customer-app",
            CommitHash = sha,
            CommittedAt = BuiltCommittedAt,
        });
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();
        return build.Id;
    }

    private async Task AddHeadAsync(
        int repositoryId, string branch, string sha, IReadOnlyList<string>? commits = null,
        bool forced = false, bool isDefault = false, DateTime? deletedAt = null)
    {
        await using var ctx = _db.NewContext();
        var list = (commits ?? [sha]).Select(c => new GitHubPushCommit(c, "Message for " + c[..7])).ToList();
        ctx.OeRepositoryBranchHeads.Add(new OeRepositoryBranchHead
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectRepositoryId = repositoryId,
            Branch = branch,
            HeadSha = sha,
            PushedAt = DateTime.UtcNow,
            PusherLogin = "erik",
            Forced = forced,
            CommitCount = list.Count,
            CommitsJson = JsonSerializer.Serialize(list),
            IsDefaultBranch = isDefault,
            DeletedAt = deletedAt,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task AddMergedAsync(int repositoryId, int number, string baseBranch, DateTime mergedAt, string mergeSha)
    {
        await using var ctx = _db.NewContext();
        ctx.OeRepositoryMergedPullRequests.Add(new OeRepositoryMergedPullRequest
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectRepositoryId = repositoryId,
            Number = number,
            Title = $"Pull request {number}",
            BaseBranch = baseBranch,
            MergeSha = mergeSha,
            MergedAt = mergedAt,
            AuthorLogin = "erik",
        });
        await ctx.SaveChangesAsync();
    }
}
