namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// A project whose Business Central solution the Object Explorer compiles from
/// source. Groups one or more <see cref="ProjectRepository"/> rows (Azure DevOps
/// or GitHub) that the project-build pipeline clones, compiles, and ingests as a
/// <c>project</c>-kind <see cref="Release"/>. Org-scoped and soft-deletable, like
/// the rest of the Object Explorer admin surface. See
/// <c>.design/object-explorer-project-builds.md</c>.
/// </summary>
public class Project
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>
    /// Project-facing label used to build the Release label
    /// (<c>"{Name} on BC {Major}.{Minor}"</c>). Unique per org among active rows.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional per-project BC localisation/country override for symbol
    /// resolution (e.g. <c>dk</c>). When null the build falls back to the org
    /// default and then <c>w1</c>. See "Symbol resolution" in the design doc.
    /// </summary>
    public string? DefaultArtifactCountry { get; set; }

    /// <summary>
    /// Deprecated and unused since the Artifacts work made builds user-initiated:
    /// a background sweep has no user whose per-user token to clone with, so the
    /// nightly auto-build path was removed. The column is retained until the
    /// Artifacts model slice drops it. See <c>.design/artifacts.md</c>.
    /// </summary>
    public bool AutoBuildEnabled { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker. Hidden from the admin list unless restored.</summary>
    public DateTime? DeletedAt { get; set; }

    public ICollection<ProjectRepository> Repositories { get; set; } = new List<ProjectRepository>();

    /// <summary>
    /// Operator-supplied third-party symbols (<see cref="ProjectSymbol"/>) the build
    /// merges into the symbol cache — the manual-symbols recovery path for a
    /// dependency absent from both the repos' <c>.alpackages/</c> and any Microsoft
    /// artifact. See <c>.design/object-explorer-project-builds.md</c>.
    /// </summary>
    public ICollection<ProjectSymbol> Symbols { get; set; } = new List<ProjectSymbol>();
}
