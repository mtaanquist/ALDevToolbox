namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// A named build configuration that belongs to a <see cref="Project"/>. A project
/// has <em>multiple</em> pipelines on purpose: different customer environments get
/// different subsets of extensions (and, in future, different delivery targets), so
/// a pipeline is "a flow of its own" rather than a single build. Running a pipeline
/// produces a <see cref="ProjectBuild"/>; the pipeline owns the extension selection
/// (<see cref="RequestedAppIdsJson"/>), and each build snapshots it at run time.
/// Org-scoped via the standard query filter; soft-deleted. See
/// <c>.design/artifacts.md</c>.
/// </summary>
public class Pipeline
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>The project (customer) this pipeline belongs to. Pipelines ride along on the project's lifecycle.</summary>
    public int ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>
    /// The user who created the pipeline — its owner of record. Nullable
    /// (<c>ON DELETE SET NULL</c>) so a pipeline outlives the account that created
    /// it; management rights come from the parent project's owner via
    /// <c>ProjectAccess</c>, not this column.
    /// </summary>
    public int? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    /// <summary>Display name, unique per project among active rows.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The extensions this pipeline compiles, as a JSON array of app-id GUID
    /// strings. <c>null</c> means "build every discovered extension" — the default,
    /// and what the backfilled <c>Default</c> pipeline carries. Copied onto each
    /// <see cref="ProjectBuild.RequestedAppIdsJson"/> at run time so the build is a
    /// faithful snapshot even after the pipeline's selection is later edited.
    /// </summary>
    public string? RequestedAppIdsJson { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker. Hidden from lists unless restored; past builds stay reachable.</summary>
    public DateTime? DeletedAt { get; set; }

    public ICollection<ProjectBuild> Builds { get; set; } = new List<ProjectBuild>();
}
