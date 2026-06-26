namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// The commit one repository was pinned at for a <see cref="ProjectBuild"/>. A
/// build is identified by the <em>set</em> of these (one per successfully cloned
/// repo), not a single hash — so the changelog and "what was built" are answerable
/// per repository. See <c>.design/artifacts.md</c>.
/// </summary>
public class ProjectBuildRepoCommit
{
    public int Id { get; set; }

    /// <summary>Owning organisation (denormalised from the build). EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int ProjectBuildId { get; set; }
    public ProjectBuild? ProjectBuild { get; set; }

    /// <summary>
    /// The repository this commit belongs to. Nullable (<c>ON DELETE SET NULL</c>)
    /// so removing a repo from the project doesn't erase the provenance of past
    /// builds that used it; <see cref="RepoUrl"/> / <see cref="RepoDisplayName"/>
    /// keep the row legible afterwards.
    /// </summary>
    public int? ProjectRepositoryId { get; set; }
    public ProjectRepository? ProjectRepository { get; set; }

    /// <summary>The repository's clone URL, snapshotted so the row stays meaningful if the repo is later removed.</summary>
    public string RepoUrl { get; set; } = string.Empty;

    /// <summary>The repository's display label at build time, snapshotted alongside <see cref="RepoUrl"/>.</summary>
    public string RepoDisplayName { get; set; } = string.Empty;

    /// <summary>The full Git commit SHA (HEAD of the cloned repo at build time).</summary>
    public string CommitHash { get; set; } = string.Empty;

    /// <summary>The committer date of <see cref="CommitHash"/> (UTC). Null when it couldn't be read.</summary>
    public DateTime? CommittedAt { get; set; }
}
