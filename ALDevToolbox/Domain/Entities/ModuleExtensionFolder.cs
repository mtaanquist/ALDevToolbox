namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One folder in a <see cref="Module"/>'s extension layout. Recursive folder
/// tree, mirroring <see cref="WorkspaceExtensionFolder"/>, but rooted at a
/// module rather than a template-declared extension. When the user picks the
/// module on the New Workspace form, the generator clones this tree (and its
/// <see cref="Files"/>) into the workspace as another extension folder.
/// </summary>
public class ModuleExtensionFolder
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }

    public int ModuleId { get; set; }
    public Module? Module { get; set; }

    public int? ParentFolderId { get; set; }
    public ModuleExtensionFolder? ParentFolder { get; set; }

    public int Ordering { get; set; }

    /// <summary>Single path segment; same rules as <see cref="WorkspaceExtensionFolder.Path"/>.</summary>
    public string Path { get; set; } = string.Empty;

    public List<ModuleExtensionFolder> Folders { get; set; } = new();
    public List<ModuleExtensionFile> Files { get; set; } = new();
}
