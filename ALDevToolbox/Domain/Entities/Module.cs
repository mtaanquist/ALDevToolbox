namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A reusable module the user can select on the New Workspace form. Each module
/// declares its own extension folder/file tree; when the user picks it, the
/// generator clones that tree into the workspace as another extension. Modules
/// also carry their own AL dependencies (e.g. the runtime DLLs / system apps
/// they depend on) which are added to the cloned extension's <c>app.json</c>.
/// </summary>
public class Module
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>URL-safe unique key (e.g. <c>document-capture</c>). Used for
    /// admin URLs and the dependency reference target; not the ZIP folder name.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Display name (e.g. <c>Document Capture</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// PascalCase name used for both the ZIP folder of the cloned extension
    /// (e.g. <c>DocumentCapture/</c>) and the rendered AL extension name
    /// (after <c>{{extension_prefix}}</c> substitution: <c>"ACME DocumentCapture"</c>).
    /// Kept separate from <see cref="Key"/> so admins can pick a URL slug that
    /// differs from the folder layout AL developers actually see.
    /// </summary>
    public string ExtensionName { get; set; } = string.Empty;

    /// <summary>
    /// Optional override for how many object ids this module reserves. Falls
    /// back to <see cref="RuntimeTemplate.ModuleIdRangeSize"/> when null.
    /// </summary>
    public int? IdRangeSize { get; set; }

    public bool Deprecated { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    /// <summary>The dependencies this module appends to its cloned extension's <c>app.json</c>.</summary>
    public List<ModuleDependency> Dependencies { get; set; } = new();

    /// <summary>
    /// Top-level folders in the module's extension layout. Children live
    /// recursively in <see cref="ModuleExtensionFolder.Folders"/>; files attach
    /// at any depth via <see cref="ModuleExtensionFolder.Files"/>.
    /// </summary>
    public List<ModuleExtensionFolder> ExtensionFolders { get; set; } = new();
}
