namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A reusable module the user can select on the New Workspace form. Each module
/// contributes its own extension folder to the generated output and adds a
/// fixed set of dependencies to that extension's <c>app.json</c>.
/// </summary>
public class Module
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>URL-safe unique key (e.g. <c>document-capture</c>).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Display name (e.g. <c>Document Capture</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional override for how many object ids this module reserves. Falls
    /// back to <see cref="RuntimeTemplate.ModuleIdRangeSize"/> when null.
    /// </summary>
    public int? IdRangeSize { get; set; }

    public bool Deprecated { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    /// <summary>The dependencies this module appends to its generated extension's <c>app.json</c>.</summary>
    public List<ModuleDependency> Dependencies { get; set; } = new();
}
