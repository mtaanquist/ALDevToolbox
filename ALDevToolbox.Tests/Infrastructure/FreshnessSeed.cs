using System.Text.Json;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.GitHub;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Seeds what <c>BuildFreshnessService</c> reads (#963) for the surfaces' tests (#964): a
/// GitHub repository on a solution, a successful build of a pipeline pinning a commit in
/// it, the head a push stored for the watched branch, and pull requests that merged into
/// it. The same rows the service's own tests seed, shared by the three page tests.
/// </summary>
internal static class FreshnessSeed
{
    public const string Built = "1111111111111111111111111111111111111111";
    public const string Newer = "2222222222222222222222222222222222222222";
    public const string Newest = "3333333333333333333333333333333333333333";

    /// <summary>When the built commit was made; pull requests merged after it count as "since".</summary>
    public static readonly DateTime BuiltCommittedAt = DateTime.UtcNow.AddDays(-2);

    public static async Task<int> AddRepositoryAsync(TestDb db, int projectId, string name = "cronus-base")
    {
        await using var ctx = db.NewContext();
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

    /// <summary>A build of <paramref name="pipelineId"/> that pinned <paramref name="sha"/> in <paramref name="repositoryId"/>.</summary>
    public static async Task<int> AddBuildAsync(
        TestDb db, int projectId, int pipelineId, int repositoryId, string sha = Built,
        string status = ProjectBuildStatus.Ready, DateTime? startedAt = null)
    {
        await using var ctx = db.NewContext();
        var started = startedAt ?? DateTime.UtcNow.AddDays(-1);
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            PipelineId = pipelineId,
            Status = status,
            Branch = "main",
            StartedAt = started,
            FinishedAt = status is ProjectBuildStatus.Ready or ProjectBuildStatus.Failed ? started.AddMinutes(5) : null,
        };
        build.RepoCommits.Add(new OeProjectBuildRepoCommit
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectRepositoryId = repositoryId,
            RepoUrl = "https://github.com/cronus-dk/cronus-base.git",
            RepoDisplayName = "cronus-base",
            CommitHash = sha,
            CommittedAt = BuiltCommittedAt,
        });
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();
        return build.Id;
    }

    /// <summary>The head the last push stored for <paramref name="branch"/>; <paramref name="commits"/> oldest first.</summary>
    public static async Task AddHeadAsync(
        TestDb db, int repositoryId, string branch, string sha, IReadOnlyList<string>? commits = null,
        bool forced = false, DateTime? deletedAt = null, DateTime? pushedAt = null)
    {
        await using var ctx = db.NewContext();
        var list = (commits ?? [sha]).Select(c => new GitHubPushCommit(c, "Fix VAT rounding on " + c[..7])).ToList();
        ctx.OeRepositoryBranchHeads.Add(new OeRepositoryBranchHead
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectRepositoryId = repositoryId,
            Branch = branch,
            HeadSha = sha,
            PushedAt = pushedAt ?? DateTime.UtcNow.AddHours(-2),
            PusherLogin = "erik",
            Forced = forced,
            CommitCount = list.Count,
            CommitsJson = JsonSerializer.Serialize(list),
            IsDefaultBranch = branch == "main",
            DeletedAt = deletedAt,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    public static async Task AddMergedAsync(TestDb db, int repositoryId, int number, string title, string mergeSha)
    {
        await using var ctx = db.NewContext();
        ctx.OeRepositoryMergedPullRequests.Add(new OeRepositoryMergedPullRequest
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectRepositoryId = repositoryId,
            Number = number,
            Title = title,
            BaseBranch = "main",
            MergeSha = mergeSha,
            MergedAt = BuiltCommittedAt.AddHours(number),
            AuthorLogin = "erik",
        });
        await ctx.SaveChangesAsync();
    }
}
