namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Logical type of entity that an audit row refers to. Stored as text in the
/// database via the EF Core string conversion configured on the context.
/// </summary>
public enum AuditEntityType
{
    RuntimeTemplate,
    /// <summary>
    /// Pre-unified Core folder rows. Retained for historical audit-log reads
    /// from databases that pre-date <c>UnifyExtensions</c>; new writes go to
    /// <see cref="WorkspaceExtensionFolder"/>.
    /// </summary>
    TemplateFolder,
    /// <summary>Pre-unified Core file rows; see <see cref="TemplateFolder"/>.</summary>
    TemplateFile,
    /// <summary>Pre-unified module folder rows; see <see cref="TemplateFolder"/>.</summary>
    TemplateModuleFolder,
    /// <summary>Pre-unified module file rows; see <see cref="TemplateFolder"/>.</summary>
    TemplateModuleFile,
    WorkspaceExtension,
    WorkspaceExtensionFolder,
    WorkspaceExtensionFile,
    WorkspaceExtensionDependency,
    ModuleExtensionFolder,
    ModuleExtensionFile,
    RuntimeTemplateDefaultModule,
    Module,
    ModuleDependency,
    WellKnownDependency,
    ApplicationVersion,
    User,
    SignupRequest,
    OrganizationSettings,
    OrganizationAsset,
    OrganizationFile,
    SystemSettings,
    Backup,
    Invite,
    Recipe,
    RecipeFile,
    RecipeSuggestion,
    RecipeSuggestionFile,
    PersonalAccessToken,
    /// <summary>A release pipeline (delivery target) — create/edit/delete of the where+how of a deploy.</summary>
    ReleasePipeline,
    /// <summary>
    /// A customer project — audited only for its Business Central connection/secret
    /// changes (the interceptor filters out discovery-cache churn and name edits).
    /// </summary>
    Project,
}

/// <summary>The kind of change captured by an audit row.</summary>
public enum AuditAction
{
    Created,
    Updated,
    Deleted,
}

/// <summary>
/// One row per write to any of the audited tables. Filled in by the
/// <c>AuditInterceptor</c> on <c>SaveChanges</c> rather than by service code.
/// </summary>
public class AuditLogEntry
{
    public int Id { get; set; }

    /// <summary>UTC instant the change was committed.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// <c>"display_name &lt;email&gt;"</c> of the acting user. <c>"unknown"</c>
    /// when no signed-in user could be resolved (e.g. seed-time inserts).
    /// </summary>
    public string ChangedBy { get; set; } = string.Empty;

    /// <summary>FK to <c>users</c>. Null for changes that happen pre-login (seed, bootstrap).</summary>
    public int? ChangedByUserId { get; set; }
    public User? ChangedByUser { get; set; }

    /// <summary>Owning organisation. Null for changes that have no org context (seed bootstrap rows).</summary>
    public int? OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public AuditEntityType EntityType { get; set; }

    /// <summary>Primary key of the affected row in its own table.</summary>
    public int EntityId { get; set; }

    public AuditAction Action { get; set; }

    /// <summary>
    /// JSON snapshot of the row's state *before* the change. <c>null</c> for
    /// <see cref="AuditAction.Created"/>, where there is no prior state.
    /// </summary>
    public string? SnapshotJson { get; set; }
}
