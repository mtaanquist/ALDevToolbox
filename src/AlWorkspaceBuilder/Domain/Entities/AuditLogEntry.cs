namespace AlWorkspaceBuilder.Domain.Entities;

/// <summary>
/// Logical type of entity that an audit row refers to. Stored as text in the
/// database via the EF Core string conversion configured on the context.
/// </summary>
public enum AuditEntityType
{
    RuntimeTemplate,
    TemplateFolder,
    Module,
    ModuleDependency,
    WellKnownDependency,
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

    /// <summary>The honour-system display name supplied at sign-in.</summary>
    public string ChangedBy { get; set; } = string.Empty;

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
