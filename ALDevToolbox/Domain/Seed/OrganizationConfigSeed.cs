namespace ALDevToolbox.Domain.Seed;

/// <summary>
/// In-memory representation of <c>organization-config.toml</c> in an export
/// archive (Milestone P3.14). Round-trips an organisation's defaults block,
/// always-included files, and logo so a snapshot import restores the full
/// admin-visible configuration.
/// </summary>
public class OrganizationConfigSeedFile
{
    public OrganizationSettingsSeed Settings { get; set; } = new();
    public OrganizationLogoSeed? Logo { get; set; }
    public List<OrganizationFileSeed> File { get; set; } = new();
}

public class OrganizationSettingsSeed
{
    public string DefaultPublisher { get; set; } = string.Empty;
    public int DefaultIdRangeFrom { get; set; }
    public int DefaultIdRangeTo { get; set; }
    public string DefaultBrief { get; set; } = string.Empty;
    public string DefaultCoreDescription { get; set; } = string.Empty;

    /// <summary>
    /// JSON template for the workspace's <c>{ShortName}.code-workspace</c>
    /// file. Empty in pre-Issue-#61 exports — readers default to the in-app
    /// fallback (<see cref="ValueObjects.OrganizationDefaults.CodeWorkspaceJson"/>).
    /// </summary>
    public string CodeWorkspaceJson { get; set; } = string.Empty;
}

public class OrganizationLogoSeed
{
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Logo bytes encoded as base64. Capped at 256 KB upstream.</summary>
    public string ContentBase64 { get; set; } = string.Empty;
}

public class OrganizationFileSeed
{
    public string Path { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool MustacheEnabled { get; set; }
}
