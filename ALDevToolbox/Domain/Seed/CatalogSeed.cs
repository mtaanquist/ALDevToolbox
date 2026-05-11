namespace ALDevToolbox.Domain.Seed;

/// <summary>
/// In-memory representation of the well-known-dependency catalogue TOML
/// document. Used by the export pipeline to serialise
/// <c>well_known_dependencies</c> rows for off-site storage.
/// </summary>
public class CatalogSeedFile
{
    public List<WellKnownDependencySeed> Dependency { get; set; } = new();
}

public class WellKnownDependencySeed
{
    public string DepId { get; set; } = string.Empty;
    public string DepName { get; set; } = string.Empty;
    public string DepPublisher { get; set; } = string.Empty;
    public string DepVersionDefault { get; set; } = string.Empty;
    public string? Category { get; set; }
}
