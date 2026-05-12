namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A file seeded into a <see cref="ModuleExtensionFolder"/>. Cloned into the
/// workspace at generation time alongside the rest of the module's extension
/// tree; mustache substitution runs the same way as for workspace files.
/// </summary>
public class ModuleExtensionFile
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }

    public int ModuleExtensionFolderId { get; set; }
    public ModuleExtensionFolder? Folder { get; set; }

    public int Ordering { get; set; }

    public string Path { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public bool IsExample { get; set; }
}
