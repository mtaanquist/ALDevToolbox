using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A runtime template — a named layout the workspace generator can use to scaffold
/// AL extensions. Each row corresponds to an entry in the New Workspace dropdown
/// and declares an ordered list of <see cref="WorkspaceExtension"/> entries plus
/// shared id-range policy, defaults, and AppSourceCop settings.
/// </summary>
/// <remarks>
/// The pre-unified model carried a separate <c>template_folders</c> table for
/// the implicit Core extension and a <c>template_module_folders</c> table for
/// scaffolding shared across modules. Both are gone: extensions are now the
/// only unit of layout — see <c>.design/templates-and-seeding.md</c>.
/// </remarks>
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
    /// <c>15.2</c>). Per-extension overrides on <see cref="WorkspaceExtension.Runtime"/>
    /// take precedence when set.
    /// </summary>
    public string Runtime { get; set; } = string.Empty;

    /// <summary>Display name shown in the dropdown.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Caption rendered under the dropdown when this template is selected.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Optional foreign key into <c>application_versions</c>. When set, the
    /// user-facing builder forms preselect the matching curated entry. The free
    /// text default (application + platform) lives inside
    /// <see cref="Defaults"/> now.
    /// </summary>
    public int? DefaultApplicationVersionId { get; set; }

    public ApplicationVersion? DefaultApplicationVersion { get; set; }

    /// <summary>
    /// When true, the form-pre-fill resolves to the highest-ordered active
    /// <see cref="ApplicationVersion"/> at the moment a user opens the
    /// builder, rather than a fixed catalogue row. Mutually exclusive with
    /// <see cref="DefaultApplicationVersionId"/>; the validator clears the FK
    /// when this flag is set. The endpoint also accepts the same sentinel
    /// on the form submission directly so users can pick "Latest" even when
    /// the template pins a specific version.
    /// </summary>
    public bool DefaultApplicationVersionLatest { get; set; }

    /// <summary>
    /// Typed view of the rest of the <c>app.json</c> defaults plus the
    /// workspace plan's pre-fill block (publisher, target, application,
    /// platform, extension_prefix, affix, features, …). Stored on the row as
    /// a JSON column to avoid schema churn when AL adds new fields.
    /// </summary>
    public TemplateDefaults Defaults { get; set; } = new();

    /// <summary>
    /// Typed view of the <c>AppSourceCop.json</c> contents. Stored on the row as
    /// a JSON column for the same reason as <see cref="Defaults"/>.
    /// </summary>
    public AppSourceCopSettings AppSourceCop { get; set; } = new();

    /// <summary>
    /// Optional per-template additions to the workspace's
    /// <c>{ShortName}.code-workspace</c> JSON. Deep-merged on top of the
    /// organisation's base template (see
    /// <c>OrganizationSettings.CodeWorkspaceJson</c>) at generation time, with
    /// template keys winning on the <c>settings</c> block and replacing whole
    /// values elsewhere. <c>null</c> means "inherit the org template as-is".
    /// </summary>
    public string? CodeWorkspaceJson { get; set; }

    /// <summary>
    /// Inclusive lower bound of the first template-declared extension's id
    /// range, used when the extension doesn't supply its own. Subsequent
    /// auto-allocated extensions start at <see cref="ModuleIdRangeStart"/>.
    /// </summary>
    public int CoreIdRangeFrom { get; set; }

    /// <summary>Inclusive upper bound of the auto-allocated first extension's id range.</summary>
    public int CoreIdRangeTo { get; set; }

    /// <summary>The first object id assigned to a module-cloned extension (or an unannotated second template extension).</summary>
    public int ModuleIdRangeStart { get; set; }

    /// <summary>How many object ids each module / unannotated extension gets, unless it overrides this.</summary>
    public int ModuleIdRangeSize { get; set; }

    /// <summary>
    /// When true the template is hidden from the user-facing dropdown but remains
    /// usable for regenerating older workspaces from the admin UI.
    /// </summary>
    public bool Deprecated { get; set; }

    /// <summary>
    /// Marks this template as the per-organisation default selected on the New
    /// Workspace and New Extension forms when the user lands without an explicit
    /// ?template= hint. Toggled via <c>TemplateService.SetDefaultAsync</c>.
    /// </summary>
    public bool IsDefault { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker. <c>null</c> means the row is active.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// The ordered list of extensions this template declares. Each entry has
    /// its own folder tree, files, and dependencies; <see cref="WorkspaceExtension.Required"/>
    /// = false entries surface as optional checkboxes on the workspace form.
    /// </summary>
    public List<WorkspaceExtension> WorkspaceExtensions { get; set; } = new();

    /// <summary>
    /// Modules pre-selected on the New Workspace form when this template is
    /// chosen, in admin-declared order. End-users can still opt out of any
    /// entry; this only seeds the initial selection.
    /// </summary>
    public List<RuntimeTemplateDefaultModule> DefaultModules { get; set; } = new();

    /// <summary>
    /// The organisation-level always-included files this template opts into.
    /// A new <see cref="OrganizationFile"/> is not emitted by any template
    /// until at least one template lists it here — admins explicitly tick
    /// the files that belong with each template.
    /// </summary>
    public List<RuntimeTemplateIncludedFile> IncludedFiles { get; set; } = new();
}
