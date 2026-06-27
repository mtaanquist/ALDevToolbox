namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One changelog entry for a <see cref="ProjectBuild"/>: a commit that landed in a
/// repository since the project's last <em>successful</em> build. Captured at build
/// time from <c>git log &lt;prev&gt;..&lt;new&gt;</c> per repo, so the Artifacts UI
/// can show "what changed" without re-cloning. The first build, a force-push
/// (non-ancestor previous commit), and an over-cap range are recorded as a single
/// summary row (see <see cref="ProjectBuildService"/>). See <c>.design/artifacts.md</c>.
/// </summary>
public class ProjectBuildCommit
{
    public int Id { get; set; }

    /// <summary>Owning organisation (denormalised from the build). EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int ProjectBuildId { get; set; }
    public ProjectBuild? ProjectBuild { get; set; }

    /// <summary>The repository this commit landed in. Nullable for a build-level summary note (first build / force-push / over-cap).</summary>
    public int? ProjectRepositoryId { get; set; }
    public ProjectRepository? ProjectRepository { get; set; }

    /// <summary>Abbreviated commit hash for display (e.g. <c>a1b2c3d</c>). Empty for a summary note.</summary>
    public string ShortHash { get; set; } = string.Empty;

    /// <summary>The commit subject (first line of the message), or the summary-note text.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>The commit author's name. Empty for a summary note.</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>The commit's committer date (UTC). Null for a summary note.</summary>
    public DateTime? CommittedAt { get; set; }

    /// <summary>Preserves the per-repo log order so the UI lists commits newest-first as Git emitted them.</summary>
    public int Ordering { get; set; }
}
