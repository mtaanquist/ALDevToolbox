namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// A pull request that merged into a solution repository, recorded from the
/// <c>pull_request</c> webhook's <c>closed</c> + <c>merged: true</c> delivery so a
/// build pipeline can say which pull requests landed on its branch since it last
/// built. One row per <c>(repository, number)</c>. See
/// <c>.design/github-integration-phase2.md</c>, "Branch watching" (#963).
/// </summary>
public class OeRepositoryMergedPullRequest
{
    public int Id { get; set; }

    /// <summary>Owning organisation (denormalised from the repository). EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int ProjectRepositoryId { get; set; }
    public OeProjectRepository? ProjectRepository { get; set; }

    /// <summary>The pull request number on GitHub.</summary>
    public int Number { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>The branch it merged into.</summary>
    public string BaseBranch { get; set; } = string.Empty;

    /// <summary>The commit the merge produced on <see cref="BaseBranch"/>. Empty when GitHub did not say.</summary>
    public string MergeSha { get; set; } = string.Empty;

    public DateTime MergedAt { get; set; }

    /// <summary>Who opened the pull request.</summary>
    public string AuthorLogin { get; set; } = string.Empty;
}
