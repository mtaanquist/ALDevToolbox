using ALDevToolbox.Domain.Entities;

namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// A customer/project entity the Artifacts tool builds. Groups one or more
/// <see cref="ProjectRepository"/> rows (Azure DevOps or GitHub) that the
/// project-build pipeline clones, compiles, and ingests; each build is a
/// first-class <see cref="ProjectBuild"/> that produces a <c>project</c>-kind
/// <see cref="Release"/> for object navigation. Any signed-in user may create and
/// browse projects; the <see cref="CreatedByUserId">owner</see> or an org Admin
/// manages repos, settings, builds, and deletion. Org-scoped and soft-deletable.
/// See <c>.design/artifacts.md</c>.
/// </summary>
public class Project
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>
    /// The user who created the project — its <em>owner</em>. The owner or an org
    /// Admin may add/remove repos, edit settings, trigger builds, and delete;
    /// everyone else gets read + download only. Nullable (<c>ON DELETE SET NULL</c>)
    /// so a project outlives the account that created it and so legacy rows
    /// migrated from the Object-Explorer era (which had no owner) are
    /// representable — those are admin-managed until reassigned. See
    /// <c>.design/artifacts.md</c>.
    /// </summary>
    public int? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

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

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker. Hidden from the admin list unless restored.</summary>
    public DateTime? DeletedAt { get; set; }

    // ── Discovered-extensions cache (the "New/Edit pipeline" picker) ──────
    // A denormalised cache of the extensions found by a shallow clone of the
    // project's repos, so the pipeline editor's checklist appears instantly
    // instead of cloning on every open. Filled in the background when repos
    // change and on demand via Refresh. Purely a picker convenience — the build
    // re-clones and filters by the pipeline's app-ids regardless. See
    // .design/artifacts.md.

    /// <summary>Last good discovery result — a JSON array of the discovered extensions (app-id, name, publisher, version, repo). Null until first discovered.</summary>
    public string? DiscoveredExtensionsJson { get; set; }

    /// <summary>When discovery last succeeded (drives "Last discovered …"). Null until first success.</summary>
    public DateTime? DiscoveredAt { get; set; }

    /// <summary>The last discovery failure reason (no token / clone failed / no app.json), shown when there's no usable cache. Cleared on success.</summary>
    public string? DiscoveryError { get; set; }

    public ICollection<ProjectRepository> Repositories { get; set; } = new List<ProjectRepository>();

    /// <summary>
    /// Operator-supplied third-party symbols (<see cref="ProjectSymbol"/>) the build
    /// merges into the symbol cache — the manual-symbols recovery path for a
    /// dependency absent from both the repos' <c>.alpackages/</c> and any Microsoft
    /// artifact. See <c>.design/object-explorer-project-builds.md</c>.
    /// </summary>
    public ICollection<ProjectSymbol> Symbols { get; set; } = new List<ProjectSymbol>();

    /// <summary>This project's builds (newest interesting first when ordered by the service). Reaped with the project.</summary>
    public ICollection<ProjectBuild> Builds { get; set; } = new List<ProjectBuild>();
}
