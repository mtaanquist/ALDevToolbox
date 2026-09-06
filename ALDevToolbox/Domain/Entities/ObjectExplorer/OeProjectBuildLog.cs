namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// Captured output from a <see cref="OeProjectBuild"/> — clone and <c>alc</c>
/// stdout/stderr — so a failed build is diagnosable and the Artifacts UI can offer
/// a <c>Raw log</c> download. <see cref="ProjectRepositoryId"/> is set for a
/// per-repo log (clone output) and null for an orchestration-level log (symbol
/// resolution + compile). See <c>.design/artifacts.md</c>.
/// </summary>
public class OeProjectBuildLog
{
    public int Id { get; set; }

    /// <summary>Owning organisation (denormalised from the build). EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int ProjectBuildId { get; set; }
    public OeProjectBuild? ProjectBuild { get; set; }

    /// <summary>The repository this log covers (clone output), or null for a build-level orchestration log.</summary>
    public int? ProjectRepositoryId { get; set; }
    public OeProjectRepository? ProjectRepository { get; set; }

    /// <summary>A short section label for the log (e.g. a repo display name or <c>Build</c>).</summary>
    public string Section { get; set; } = string.Empty;

    /// <summary>The captured text.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Preserves emit order so the Raw log download reads top-to-bottom as the build ran.</summary>
    public int Ordering { get; set; }

    public DateTime CreatedAt { get; set; }
}
