namespace ALDevToolbox.Domain.Seed;

/// <summary>
/// In-memory representation of a <c>Templates.seed/modules/&lt;key&gt;.toml</c>
/// file. The shape mirrors the TOML directly; the <c>SeedService</c> maps it
/// onto the EF entities.
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
