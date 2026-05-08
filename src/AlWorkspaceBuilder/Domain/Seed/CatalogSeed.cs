namespace AlWorkspaceBuilder.Domain.Seed;

/// <summary>
/// In-memory representation of <c>Templates.seed/catalog/well-known-deps.toml</c>.
/// Each entry becomes a row in the <c>well_known_dependencies</c> table.
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
