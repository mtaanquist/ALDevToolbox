namespace ALDevToolbox.Domain.Seed;

/// <summary>
/// In-memory representation of a single module TOML document. Used by the
/// export pipeline to serialise module rows for off-site storage.
/// </summary>
public class ModuleSeedFile
{
    public ModuleSeed Module { get; set; } = new();
}

/// <summary>The <c>[module]</c> table.</summary>
public class ModuleSeed
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int? IdRangeSize { get; set; }

    /// <summary>
    /// Mirrors <see cref="ALDevToolbox.Domain.Entities.Module.Deprecated"/> so
    /// the export/import round-trip preserves the "hidden from end-user
    /// pickers but still available for regeneration" flag.
    /// </summary>
    public bool Deprecated { get; set; }

    public List<ModuleDependencySeed> Dependencies { get; set; } = new();
}

/// <summary>One <c>[[module.dependencies]]</c> entry.</summary>
public class ModuleDependencySeed
{
    public string DepId { get; set; } = string.Empty;
    public string DepName { get; set; } = string.Empty;
    public string DepPublisher { get; set; } = string.Empty;
    public string DepVersion { get; set; } = string.Empty;
}
