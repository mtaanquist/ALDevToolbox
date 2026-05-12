namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A file seeded into a <see cref="WorkspaceExtensionFolder"/>. Files attach at
/// any depth in the folder tree; mustache substitution runs on <c>.al</c>
/// contents at generation time.
/// </summary>
public class WorkspaceExtensionFile
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }

    public int WorkspaceExtensionFolderId { get; set; }
    public WorkspaceExtensionFolder? Folder { get; set; }

    /// <summary>Display order within the parent folder.</summary>
    public int Ordering { get; set; }

    /// <summary>
    /// File name (basename within the parent folder; no slashes). The full
    /// generated path is composed by joining the folder's resolved path with
    /// this value.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Raw file content. Mustache variables are substituted at generation time.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// When true, the file ships only when the user ticks "include examples"
    /// on the New Workspace form. Mirrors the legacy <c>example/</c> folder
    /// convention — now expressible per file rather than per folder.
    /// </summary>
    public bool IsExample { get; set; }
}
