using Tomlyn.Serialization;

namespace ALDevToolbox.Domain.Seed;

/// <summary>
/// In-memory representation of a <c>template.toml</c> document. Used by the
/// admin TOML editor (<see cref="Services.TemplateTomlMapper"/>) and by the
/// export pipeline (<see cref="Services.ExportService"/>) so admins can paste
/// or download templates in a stable text format.
/// </summary>
/// <remarks>
/// The TOML files mix conventions: <c>[template]</c> / <c>[[folders]]</c> use
/// <c>snake_case</c>, while <c>[defaults]</c> mirrors the AL <c>app.json</c>
/// shape and uses <c>camelCase</c>. The whole graph deserialises under a
/// global <c>SnakeCaseLower</c> policy, with <see cref="TomlPropertyNameAttribute"/>
/// overriding the camelCase outliers individually. AppSourceCop is no longer
/// a top-level table — templates that want an <c>AppSourceCop.json</c>
/// declare it as a <c>[[folders.files]]</c> entry under a folder with an
/// empty <c>path</c> (extension root).
/// </remarks>
public class TemplateSeed
{
    public TemplateMetaSeed Template { get; set; } = new();
    public DefaultsSeed Defaults { get; set; } = new();

    /// <summary>
    /// The <c>[code_workspace]</c> table — verbatim <c>.code-workspace</c>
    /// content with a <c>{{paths}}</c> placeholder for the generator to
    /// expand. Round-trips through both the admin TOML editor and the export
    /// bundle so admins can edit per-template VS Code settings, analyzer
    /// lists, ruleset paths and extension recommendations.
    /// </summary>
    [TomlPropertyName("code_workspace")]
    public CodeWorkspaceSeed CodeWorkspace { get; set; } = new();

    public List<FolderSeed> Folders { get; set; } = new();

    /// <summary>
    /// <c>[[module_folders]]</c> entries — the folder layout emitted into every
    /// generated module extension. Empty by default. Same shape as
    /// <see cref="Folders"/>.
    /// </summary>
    public List<FolderSeed> ModuleFolders { get; set; } = new();
}

/// <summary>The <c>[code_workspace]</c> table — see <see cref="TemplateSeed.CodeWorkspace"/>.</summary>
public class CodeWorkspaceSeed
{
    /// <summary>Verbatim file content; emitted as a TOML multi-line basic string for readability.</summary>
    public string Content { get; set; } = string.Empty;
}

/// <summary>The <c>[template]</c> table — identifying metadata and id ranges.</summary>
public class TemplateMetaSeed
{
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Runtime version, e.g. <c>"15"</c> or <c>"15.2"</c>. Older TOML
    /// documents have it as a bare integer; the admin TOML pipeline
    /// normalises both forms before deserialisation so the schema stays
    /// welcoming.
    /// </summary>
    public string Runtime { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string DefaultApplication { get; set; } = string.Empty;
    public string DefaultPlatform { get; set; } = string.Empty;
    public int CoreIdRangeFrom { get; set; } = 90000;
    public int CoreIdRangeTo { get; set; } = 90999;
    public int ModuleIdRangeStart { get; set; } = 91000;
    public int ModuleIdRangeSize { get; set; } = 200;

    /// <summary>
    /// Module keys pre-selected when a user picks this template on the New
    /// Workspace form. Order matches the array order in the TOML file. Unknown
    /// keys are dropped at seed time with a warning rather than failing the
    /// import — admins can fix typos through the UI later.
    /// </summary>
    public List<string> DefaultModules { get; set; } = new();

    /// <summary>
    /// Optional key into the curated <c>application_versions</c> catalogue
    /// (Milestone P2.4). When set and resolvable at seed time, the template's
    /// <c>DefaultApplicationVersionId</c> FK is filled in so the user-facing
    /// builder forms preselect this entry. Unresolved keys log a warning and
    /// the template falls back to its free-text <c>default_application</c> /
    /// <c>runtime</c> values.
    /// </summary>
    public string? DefaultApplicationVersion { get; set; }
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

    /// <summary>
    /// Object-name affix string. Drives the <c>{{prefix}}</c>,
    /// <c>{{suffix}}</c> and <c>{{affix}}</c> mustache variables at
    /// generation time. Camel-cased in TOML (<c>affix = "ACME"</c>) to
    /// match the surrounding <c>[defaults]</c> conventions.
    /// </summary>
    public string Affix { get; set; } = string.Empty;

    /// <summary>
    /// Whether <see cref="Affix"/> is applied as a prefix or suffix.
    /// Default <c>Prefix</c> matches the migrated <c>mandatoryPrefix</c>
    /// semantic. Serialised as <c>"Prefix"</c> / <c>"Suffix"</c>.
    /// </summary>
    [TomlPropertyName("affixType")]
    public ALDevToolbox.Domain.ValueObjects.AffixType AffixType { get; set; } =
        ALDevToolbox.Domain.ValueObjects.AffixType.Prefix;
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

/// <summary>One <c>[[folders]]</c> entry — a relative folder path plus its seeded files.</summary>
public class FolderSeed
{
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Files seeded into the folder. Empty list means the folder generates
    /// with a single <c>.gitkeep</c> placeholder. Mustache substitution runs
    /// at generation time, not at parse time.
    /// </summary>
    public List<FolderFileSeed> Files { get; set; } = new();
}

/// <summary>One <c>[[folders.files]]</c> entry — a path/content pair.</summary>
public class FolderFileSeed
{
    /// <summary>Relative path inside the folder, forward-slash separated.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Raw file content. Mustache variables are substituted at generation time.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// When true, the file is treated as scaffolding the end user opted into
    /// via the "Include example AL files" checkbox; clearing the checkbox
    /// skips it at generation time. Defaults to false so non-example files
    /// are always emitted. Serialised as a <c>is_example = true</c> line
    /// (omitted when false to keep TOML diffs quiet).
    /// </summary>
    public bool IsExample { get; set; }
}
