using Tomlyn.Serialization;

namespace ALDevToolbox.Domain.Seed;

/// <summary>
/// In-memory representation of a <c>Templates.seed/runtime-*/template.toml</c>
/// file. Tomlyn deserialises directly into this graph; the
/// <c>SeedService</c> then maps it onto the EF entities.
/// </summary>
/// <remarks>
/// The TOML files mix conventions: <c>[template]</c> / <c>[[folders]]</c> use
/// <c>snake_case</c>, while <c>[defaults]</c> and <c>[appSourceCop]</c> mirror
/// the AL JSON shapes and use <c>camelCase</c>. The whole graph deserialises
/// under a global <c>SnakeCaseLower</c> policy, with <see cref="TomlPropertyNameAttribute"/>
/// overriding the camelCase outliers individually.
/// </remarks>
public class TemplateSeed
{
    public TemplateMetaSeed Template { get; set; } = new();
    public DefaultsSeed Defaults { get; set; } = new();

    [TomlPropertyName("appSourceCop")]
    public AppSourceCopSeed AppSourceCop { get; set; } = new();

    public List<FolderSeed> Folders { get; set; } = new();
}

/// <summary>The <c>[template]</c> table — identifying metadata and id ranges.</summary>
public class TemplateMetaSeed
{
    public string Key { get; set; } = string.Empty;
    public int Runtime { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string DefaultApplication { get; set; } = string.Empty;
    public string DefaultPlatform { get; set; } = string.Empty;
    public int CoreIdRangeFrom { get; set; } = 90000;
    public int CoreIdRangeTo { get; set; } = 90999;
    public int ModuleIdRangeStart { get; set; } = 91000;
    public int ModuleIdRangeSize { get; set; } = 200;
}

/// <summary>The <c>[defaults]</c> table — merged into every generated <c>app.json</c>.</summary>
public class DefaultsSeed
{
    public string Publisher { get; set; } = string.Empty;
    public string Target { get; set; } = "Cloud";
    public string? Url { get; set; }
    public string? Logo { get; set; }
    public List<string> Features { get; set; } = new();

    [TomlPropertyName("supportedLocales")]
    public List<string> SupportedLocales { get; set; } = new();

    [TomlPropertyName("resourceExposurePolicy")]
    public ResourceExposurePolicySeed ResourceExposurePolicy { get; set; } = new();
}

public class ResourceExposurePolicySeed
{
    [TomlPropertyName("allowDebugging")]
    public bool AllowDebugging { get; set; }

    [TomlPropertyName("allowDownloadingSource")]
    public bool AllowDownloadingSource { get; set; }

    [TomlPropertyName("includeSourceInSymbolFile")]
    public bool IncludeSourceInSymbolFile { get; set; }
}

/// <summary>The <c>[appSourceCop]</c> table — copied verbatim into <c>AppSourceCop.json</c>.</summary>
public class AppSourceCopSeed
{
    [TomlPropertyName("mandatoryPrefix")]
    public string MandatoryPrefix { get; set; } = string.Empty;

    [TomlPropertyName("supportedCountries")]
    public List<string> SupportedCountries { get; set; } = new();
}

/// <summary>One <c>[[folders]]</c> entry — a single relative folder path.</summary>
public class FolderSeed
{
    public string Path { get; set; } = string.Empty;
    public string? Example { get; set; }
}
