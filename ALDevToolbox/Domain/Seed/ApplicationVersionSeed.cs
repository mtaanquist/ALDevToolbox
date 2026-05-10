namespace ALDevToolbox.Domain.Seed;

/// <summary>
/// In-memory representation of a
/// <c>Templates.seed/application-versions/&lt;key&gt;.toml</c> file. The shape
/// mirrors the TOML directly; the <c>SeedService</c> maps it onto the EF
/// <see cref="Domain.Entities.ApplicationVersion"/> entity.
/// </summary>
public class ApplicationVersionSeedFile
{
    public ApplicationVersionSeed Version { get; set; } = new();
}

/// <summary>The <c>[version]</c> table in an application-version seed file.</summary>
public class ApplicationVersionSeed
{
    /// <summary>URL-safe unique key (e.g. <c>bc-2026-w1</c>).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Friendly label shown in the user-facing select.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Four-part application version (e.g. <c>28.0.0.0</c>).</summary>
    public string Application { get; set; } = string.Empty;

    /// <summary>Runtime version (e.g. <c>"28.0"</c>).</summary>
    public string Runtime { get; set; } = string.Empty;

    /// <summary>Display order; the user-facing select honours it.</summary>
    public int Ordering { get; set; }
}
