namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One of the AL apps a <see cref="Module"/> declares it depends on. Rows are
/// ordered per module via <see cref="Ordering"/> so the dependency list in
/// <c>app.json</c> stays stable between regenerations.
/// </summary>
public class ModuleDependency
{
    public int Id { get; set; }

    /// <summary>Denormalised owning organisation; mirrors the module's value.</summary>
    public int OrganizationId { get; set; }

    /// <summary>Owning module. Cascade-deleted with the parent.</summary>
    public int ModuleId { get; set; }
    public Module? Module { get; set; }

    /// <summary>Position within the parent module's dependency list.</summary>
    public int Ordering { get; set; }

    /// <summary>The dependency app's GUID.</summary>
    public string DepId { get; set; } = string.Empty;

    /// <summary>The dependency app's display name.</summary>
    public string DepName { get; set; } = string.Empty;

    /// <summary>The dependency app's publisher.</summary>
    public string DepPublisher { get; set; } = string.Empty;

    /// <summary>The version constraint to write into <c>app.json</c>.</summary>
    public string DepVersion { get; set; } = string.Empty;
}
