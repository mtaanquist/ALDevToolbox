using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A runtime template — a named layout the workspace generator can use to scaffold
/// an extension. Each row corresponds to an entry in the New Workspace dropdown
/// and ships with its own folder list, app.json defaults, and AppSourceCop settings.
/// </summary>
public class RuntimeTemplate
{
    /// <summary>Database identifier. Auto-incremented.</summary>
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>
    /// URL-safe unique key (e.g. <c>runtime-15</c>). Used in admin URLs and as
    /// the stable identifier when the form posts back a chosen template.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// The AL runtime version this template targets (e.g. <c>15</c> or
    /// <c>15.2</c>). BC's runtime is conceptually a Major[.Minor] string —
    /// kept as a string so newer BC releases (15.2, 15.3, …) round-trip
    /// cleanly through the seed TOML, the admin form, and the generated
    /// <c>app.json</c>'s <c>runtime</c> field.
    /// </summary>
    public string Runtime { get; set; } = string.Empty;

    /// <summary>Display name shown in the dropdown.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Caption rendered under the dropdown when this template is selected.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Free-text fallback for the <c>application</c> field (Major.Minor.Build.Revision).
    /// Superseded in Milestone P2.4 by <see cref="DefaultApplicationVersion"/>; kept as
    /// the orphan value when no curated entry matches, and as the backing field exported
    /// to <c>template.toml</c> for backward compatibility.
    /// </summary>
    public string DefaultApplication { get; set; } = string.Empty;

    /// <summary>Pre-fills the <c>platform</c> field on the New Workspace form.</summary>
    public string DefaultPlatform { get; set; } = string.Empty;

    /// <summary>
    /// Optional foreign key into <c>application_versions</c>. When set, the user-facing
    /// builder forms preselect the matching curated entry and fill both
    /// <c>application</c> and <c>runtime</c> from it. <c>null</c> means the template
    /// pre-dates the curated catalogue or no entry matches; the form then falls back
    /// to the free-text <see cref="DefaultApplication"/> / <see cref="Runtime"/> values.
    /// </summary>
    public int? DefaultApplicationVersionId { get; set; }

    /// <summary>Navigation back to the curated catalogue entry (when one is set).</summary>
    public ApplicationVersion? DefaultApplicationVersion { get; set; }

    /// <summary>
    /// Typed view of the rest of the <c>app.json</c> defaults (publisher, target,
    /// features, etc.). Stored on the row as a JSON column to avoid schema churn
    /// when AL adds new fields.
    /// </summary>
    public TemplateDefaults Defaults { get; set; } = new();

    /// <summary>
    /// Typed view of the <c>AppSourceCop.json</c> contents. Stored on the row as
    /// a JSON column for the same reason as <see cref="Defaults"/>.
    /// </summary>
    public AppSourceCopSettings AppSourceCop { get; set; } = new();

    /// <summary>Inclusive lower bound of the Core extension's id range.</summary>
    public int CoreIdRangeFrom { get; set; }

    /// <summary>Inclusive upper bound of the Core extension's id range.</summary>
    public int CoreIdRangeTo { get; set; }

    /// <summary>The first object id assigned to a module-derived extension.</summary>
    public int ModuleIdRangeStart { get; set; }

    /// <summary>How many object ids each module gets, unless it overrides this.</summary>
    public int ModuleIdRangeSize { get; set; }

    /// <summary>
    /// When true the template is hidden from the user-facing dropdown but remains
    /// usable for regenerating older workspaces from the admin UI.
    /// </summary>
    public bool Deprecated { get; set; }

    /// <summary>
    /// Marks this template as the per-organisation default selected on the New
    /// Workspace and New Extension forms when the user lands without an explicit
    /// ?template= hint. Exactly one active template per organisation should
    /// carry this flag — enforced by a filtered unique index in the DB. Toggled
    /// via <c>TemplateService.SetDefaultAsync</c>; not editable from the
    /// Create/Edit form directly.
    /// </summary>
    public bool IsDefault { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker. <c>null</c> means the row is active.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// Folders emitted into the Core extension only, ordered as they appear in
    /// the UI. Module extensions use <see cref="ModuleFolders"/> instead so
    /// Core-specific scaffolding doesn't bleed into every module ZIP.
    /// </summary>
    public List<TemplateFolder> Folders { get; set; } = new();

    /// <summary>
    /// Folders emitted into every module extension generated from this template.
    /// Empty by default — modules then ship with just <c>app.json</c>,
    /// <c>AppSourceCop.json</c>, and the static fallback placeholders
    /// (<c>libs/</c>, <c>permissionsets/</c>, <c>Translations/</c>).
    /// </summary>
    public List<TemplateModuleFolder> ModuleFolders { get; set; } = new();

    /// <summary>
    /// Modules pre-selected on the New Workspace form when this template is
    /// chosen, in admin-declared order. End-users can still opt out of any
    /// entry; this only seeds the initial selection.
    /// </summary>
    public List<RuntimeTemplateDefaultModule> DefaultModules { get; set; } = new();
}
