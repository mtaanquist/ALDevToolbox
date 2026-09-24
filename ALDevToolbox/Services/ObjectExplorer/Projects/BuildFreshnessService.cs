using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.GitHub;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// Whether a build pipeline's last successful build is still what its watched
/// branch holds, per repository: the head the <c>push</c> webhook last reported
/// against the commit the build pinned (<c>oe_project_build_repo_commits</c>).
///
/// <para>Read-only and passive. Nothing here asks GitHub anything and nothing is
/// built: a person reads the answer and decides whether to press Build. The
/// comparison is by commit SHA only - a force push makes any count of commits
/// meaningless, and the head row's <c>forced</c> flag is what says so. See
/// <c>.design/github-integration-phase2.md</c>, "Branch watching" (#963).</para>
/// </summary>
public sealed class BuildFreshnessService
{
    private readonly AppDbContext _db;
    private readonly ProjectAccess _access;

    public BuildFreshnessService(AppDbContext db, ProjectAccess access)
    {
        _db = db;
        _access = access;
    }

    /// <summary>
    /// The freshness of <paramref name="pipelineId"/>, one entry per repository of
    /// its solution (a pipeline builds every repository), or <see langword="null"/>
    /// when the pipeline does not exist in this organisation. Throws
    /// <see cref="ProjectAccessDeniedException"/> when the pipeline's solution is
    /// Private and the caller may not see it - the same gate as the pipeline pages.
    ///
    /// <para>States, checked in this order: <see cref="BuildFreshnessState.BranchGone"/>
    /// (GitHub reported the watched branch deleted), <see cref="BuildFreshnessState.NeverBuilt"/>
    /// (no successful build of this pipeline pinned a commit in the repository),
    /// <see cref="BuildFreshnessState.Unknown"/> (no head stored for the branch: the
    /// App is not sending pushes, or nothing has been pushed since),
    /// <see cref="BuildFreshnessState.UpToDate"/> (same SHA), otherwise
    /// <see cref="BuildFreshnessState.Ahead"/>.</para>
    /// </summary>
    public async Task<PipelineFreshness?> GetAsync(int pipelineId, CancellationToken ct = default)
    {
        var pipeline = await _db.OePipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId && p.DeletedAt == null)
            .Select(p => new { p.Id, p.ProjectId, p.Branch })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (pipeline is null) return null;
        await _access.EnsureCanViewAsync(pipeline.ProjectId, ct).ConfigureAwait(false);

        var repositories = await _db.OeProjectRepositories.AsNoTracking()
            .Where(r => r.ProjectId == pipeline.ProjectId)
            .OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.DisplayName })
            .ToListAsync(ct).ConfigureAwait(false);
        var repositoryIds = repositories.Select(r => r.Id).ToList();

        // The last successful build of this pipeline, and the commit it pinned in
        // each repository. A repository added after that build, or whose clone
        // failed in it, has no commit there and reads as never built.
        var lastBuild = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.PipelineId == pipelineId && b.Status == ProjectBuildStatus.Ready)
            .OrderByDescending(b => b.StartedAt).ThenByDescending(b => b.Id)
            .Select(b => new { b.Id, b.StartedAt, b.FinishedAt })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var builtByRepository = new Dictionary<int, (string Sha, DateTime? CommittedAt)>();
        if (lastBuild is not null)
        {
            var commits = await _db.OeProjectBuildRepoCommits.AsNoTracking()
                .Where(c => c.ProjectBuildId == lastBuild.Id && c.ProjectRepositoryId != null && c.CommitHash != "")
                .Select(c => new { RepositoryId = c.ProjectRepositoryId!.Value, c.CommitHash, c.CommittedAt })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var c in commits) builtByRepository[c.RepositoryId] = (c.CommitHash, c.CommittedAt);
        }

        var heads = await _db.OeRepositoryBranchHeads.AsNoTracking()
            .Where(h => repositoryIds.Contains(h.ProjectRepositoryId))
            .ToListAsync(ct).ConfigureAwait(false);
        var merged = await _db.OeRepositoryMergedPullRequests.AsNoTracking()
            .Where(m => repositoryIds.Contains(m.ProjectRepositoryId))
            .ToListAsync(ct).ConfigureAwait(false);

        var result = new List<RepositoryFreshness>(repositories.Count);
        foreach (var repository in repositories)
        {
            // The branch this repository is watched on: the pipeline's own, or the
            // repository's default as the last push reported it.
            var head = pipeline.Branch is { } named
                ? heads.FirstOrDefault(h => h.ProjectRepositoryId == repository.Id
                                            && string.Equals(h.Branch, named, StringComparison.Ordinal))
                : heads.FirstOrDefault(h => h.ProjectRepositoryId == repository.Id
                                            && h.IsDefaultBranch && h.DeletedAt is null);
            var watchedBranch = pipeline.Branch ?? head?.Branch;
            (string Sha, DateTime? CommittedAt)? built = builtByRepository.TryGetValue(repository.Id, out var b) ? b : null;

            var state = head is { DeletedAt: not null } ? BuildFreshnessState.BranchGone
                : built is null ? BuildFreshnessState.NeverBuilt
                : head is null ? BuildFreshnessState.Unknown
                : string.Equals(head.HeadSha, built.Value.Sha, StringComparison.OrdinalIgnoreCase) ? BuildFreshnessState.UpToDate
                : BuildFreshnessState.Ahead;

            IReadOnlyList<MergedPullRequestSummary> mergedSince = [];
            IReadOnlyList<CommitSummary> commitsSince = [];
            var commitsComplete = true;
            if (state == BuildFreshnessState.Ahead && head is not null && built is { } pinned)
            {
                // Pull requests that merged into the watched branch after the built
                // commit was made. Best effort: time is what the record carries, and
                // the built commit's own merge is left out by SHA.
                var since = pinned.CommittedAt ?? lastBuild!.StartedAt;
                mergedSince = merged
                    .Where(m => m.ProjectRepositoryId == repository.Id
                                && string.Equals(m.BaseBranch, watchedBranch, StringComparison.Ordinal)
                                && m.MergedAt > since
                                && !string.Equals(m.MergeSha, pinned.Sha, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(m => m.MergedAt)
                    .Select(m => new MergedPullRequestSummary(m.Number, m.Title, m.AuthorLogin, m.MergedAt, m.MergeSha))
                    .ToList();

                // The stored commits after the built one. When the built commit is
                // not in the list the list is only the newest part of what changed.
                var stored = GitHubBranchActivityService.ReadCommits(head.CommitsJson);
                var at = stored.FindIndex(c => string.Equals(c.Sha, pinned.Sha, StringComparison.OrdinalIgnoreCase));
                commitsComplete = at >= 0;
                commitsSince = stored.Skip(at + 1)
                    .Reverse()
                    .Select(c => new CommitSummary(c.Sha, c.Message))
                    .ToList();
            }

            result.Add(new RepositoryFreshness(
                RepositoryId: repository.Id,
                RepositoryName: repository.DisplayName,
                Branch: watchedBranch,
                State: state,
                HeadSha: head?.HeadSha,
                PushedAt: head?.PushedAt,
                PusherLogin: head is { PusherLogin.Length: > 0 } ? head.PusherLogin : null,
                Forced: head?.Forced ?? false,
                BuiltSha: built?.Sha,
                MergedPullRequests: mergedSince,
                Commits: commitsSince,
                CommitsComplete: commitsComplete));
        }

        return new PipelineFreshness(
            PipelineId: pipeline.Id,
            Branch: pipeline.Branch,
            LastBuildId: lastBuild?.Id,
            LastBuiltAt: lastBuild is null ? null : lastBuild.FinishedAt ?? lastBuild.StartedAt,
            Repositories: result);
    }
}

/// <summary>Where one repository of a pipeline stands against the branch it watches.</summary>
public enum BuildFreshnessState
{
    /// <summary>The last successful build used the commit the branch points at now.</summary>
    UpToDate,

    /// <summary>The branch has moved past the commit the last successful build used.</summary>
    Ahead,

    /// <summary>No successful build of this pipeline has used this repository yet.</summary>
    NeverBuilt,

    /// <summary>GitHub reported the watched branch deleted.</summary>
    BranchGone,

    /// <summary>
    /// No head is stored for the watched branch: the GitHub App is not sending
    /// pushes, the repository is not on GitHub, or nothing has been pushed since
    /// branch watching arrived.
    /// </summary>
    Unknown,
}

/// <summary>A build pipeline's freshness: its branch (null = each repository's default), its last successful build, and each repository.</summary>
public sealed record PipelineFreshness(
    int PipelineId,
    string? Branch,
    int? LastBuildId,
    DateTime? LastBuiltAt,
    IReadOnlyList<RepositoryFreshness> Repositories);

/// <summary>
/// One repository's freshness.
/// </summary>
/// <param name="Branch">The branch watched in this repository; null when the pipeline follows the default branch and no push has said which that is.</param>
/// <param name="HeadSha">Where the branch points, as the last push said; null when nothing is stored.</param>
/// <param name="Forced">The last push rewrote history, so the commit list below may not be a linear "since" at all.</param>
/// <param name="BuiltSha">The commit the last successful build used; null when it never used this repository.</param>
/// <param name="MergedPullRequests">For <see cref="BuildFreshnessState.Ahead"/>: pull requests merged into the branch since the built commit, newest first.</param>
/// <param name="Commits">For <see cref="BuildFreshnessState.Ahead"/>: the stored commits after the built one, newest first.</param>
/// <param name="CommitsComplete">False when the built commit is not among the stored commits, so <paramref name="Commits"/> is only the newest part of what changed.</param>
public sealed record RepositoryFreshness(
    int RepositoryId,
    string RepositoryName,
    string? Branch,
    BuildFreshnessState State,
    string? HeadSha,
    DateTime? PushedAt,
    string? PusherLogin,
    bool Forced,
    string? BuiltSha,
    IReadOnlyList<MergedPullRequestSummary> MergedPullRequests,
    IReadOnlyList<CommitSummary> Commits,
    bool CommitsComplete);

/// <summary>A pull request that merged into a watched branch.</summary>
public sealed record MergedPullRequestSummary(int Number, string Title, string AuthorLogin, DateTime MergedAt, string MergeSha);

/// <summary>One commit on a watched branch, as a push listed it.</summary>
public sealed record CommitSummary(string Sha, string Message);
