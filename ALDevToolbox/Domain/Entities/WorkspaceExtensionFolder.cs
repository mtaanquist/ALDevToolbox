namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One folder in a <see cref="WorkspaceExtension"/>'s recursive layout. Each
/// row is a single path segment (no slashes); nesting is expressed via the
/// self-referencing <see cref="ParentFolder"/> / <see cref="Folders"/>
/// relationship. Files attach at any depth via <see cref="Files"/>, so a
/// template can hang a single AL file directly off the extension root or
/// deep inside a nested tree.
/// </summary>
/// <remarks>
/// <see cref="WorkspaceExtensionId"/> is denormalised onto every row so leaf
/// queries can scope by extension without walking the parent chain;
/// reconciliation in the service layer keeps it in sync with the parent.
/// </remarks>
public class WorkspaceExtensionFolder
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }

    /// <summary>The extension this folder belongs to. Denormalised — see remarks on the type.</summary>
    public int WorkspaceExtensionId { get; set; }
    public WorkspaceExtension? Extension { get; set; }

    /// <summary>
    /// The parent folder, or null when this folder sits at the extension's
    /// root. Self-referencing FK; cascade-deletes children with the parent.
    /// </summary>
    public int? ParentFolderId { get; set; }
    public WorkspaceExtensionFolder? ParentFolder { get; set; }

    /// <summary>Display order among sibling folders.</summary>
    public int Ordering { get; set; }

    /// <summary>
    /// Single path segment (no <c>/</c> or <c>\</c>, no <c>..</c>, non-empty).
    /// The full relative path is built by walking up the parent chain at
    /// generation time.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Child folders. Empty when this folder is a leaf.</summary>
    public List<WorkspaceExtensionFolder> Folders { get; set; } = new();

    /// <summary>Files attached directly to this folder (at any depth).</summary>
    public List<WorkspaceExtensionFile> Files { get; set; } = new();
}
