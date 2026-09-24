namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// Where one branch of a solution repository pointed the last time GitHub told us
/// it moved: one row per <c>(repository, branch)</c>, upserted by the <c>push</c>
/// webhook. It is what a build pipeline watching that branch compares its last
/// build against; nothing is built because a row changed. Rows are written only by
/// pushes, never by polling, so a repository whose App is not subscribed to Push
/// simply has none. See <c>.design/github-integration-phase2.md</c>, "Branch
/// watching" (#963).
/// </summary>
public class OeRepositoryBranchHead
{
    public int Id { get; set; }

    /// <summary>Owning organisation (denormalised from the repository). EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>
    /// The solution repository the push was for. Two solutions tracking the same
    /// GitHub repository each get their own row, because each has its own
    /// <see cref="OeProjectRepository"/>.
    /// </summary>
    public int ProjectRepositoryId { get; set; }
    public OeProjectRepository? ProjectRepository { get; set; }

    /// <summary>The branch name without <c>refs/heads/</c>.</summary>
    public string Branch { get; set; } = string.Empty;

    /// <summary>The full commit SHA the branch points at; for a deleted branch, the one it pointed at last.</summary>
    public string HeadSha { get; set; } = string.Empty;

    /// <summary>When the push happened (GitHub's <c>repository.pushed_at</c>), UTC.</summary>
    public DateTime PushedAt { get; set; }

    /// <summary>Who pushed, as GitHub named them. Empty when the payload did not say.</summary>
    public string PusherLogin { get; set; } = string.Empty;

    /// <summary>
    /// The last push rewrote history (GitHub's <c>forced</c>). After one, "how many
    /// commits behind" has no meaning; this is what lets a surface say why.
    /// </summary>
    public bool Forced { get; set; }

    /// <summary>How many commits the last push listed. GitHub lists at most twenty.</summary>
    public int CommitCount { get; set; }

    /// <summary>
    /// The newest commits seen on the branch, oldest first, as a JSON array of
    /// <c>{ "sha", "message" }</c>, at most ten. A push that fast-forwards from the
    /// stored head appends to the list; a forced push or a jump from elsewhere
    /// replaces it.
    /// </summary>
    public string CommitsJson { get; set; } = "[]";

    /// <summary>
    /// Whether this is the repository's default branch, as the last push to the
    /// repository reported it. A pipeline with no branch of its own watches this row.
    /// </summary>
    public bool IsDefaultBranch { get; set; }

    /// <summary>Set when GitHub reported the branch deleted. Kept, not removed, so a surface can say the branch is gone.</summary>
    public DateTime? DeletedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
