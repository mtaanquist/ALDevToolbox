using ALDevToolbox.Domain.Entities;
using Tomlyn.Serialization;

namespace ALDevToolbox.Domain.Seed;

/// <summary>
/// In-memory representation of a <c>template.toml</c> document under the
/// unified-extensions model (Issue #54). Used by
/// <see cref="Services.TemplateTomlMapper"/> for round-trip serialisation
/// between the admin TOML editor and a structured authoring payload.
/// </summary>
/// <remarks>
/// The TOML files mix conventions: <c>[template]</c> / <c>[[extensions]]</c>
/// use <c>snake_case</c>; <c>[defaults]</c> and <c>[appSourceCop]</c> mirror
/// AL's JSON shape and use <c>camelCase</c>. The graph deserialises under a
/// global <c>SnakeCaseLower</c> policy, with <see cref="TomlPropertyNameAttribute"/>
/// overriding the camelCase outliers individually.
/// </remarks>
public class TemplateSeed
{
    public TemplateMetaSeed Template { get; set; } = new();
    public DefaultsSeed Defaults { get; set; } = new();

    [TomlPropertyName("appSourceCop")]
    public AppSourceCopSeed AppSourceCop { get; set; } = new();

    /// <summary>
    /// Optional per-template overlay for <c>{ShortName}.code-workspace</c>
    /// (issue #61). Absent in most templates — the org's base template is
    /// used unchanged. When present, the JSON is deep-merged onto the org
    /// base at generation time. Serialised by hand so the embedded JSON
    /// renders as a TOML triple-quoted literal rather than an escaped
    /// single-line blob.
    /// </summary>
    [TomlPropertyName("workspace_settings")]
    public WorkspaceSettingsSeed? WorkspaceSettings { get; set; }

    /// <summary>
    /// Ordered extensions declared by the template. Required entries are
    /// always emitted; <c>required = false</c> entries surface as opt-in
    /// checkboxes on New Workspace.
    /// </summary>
    public List<ExtensionSeed> Extensions { get; set; } = new();
}

/// <summary>
/// The <c>[workspace_settings]</c> table — per-template overlay for the
/// generated <c>{ShortName}.code-workspace</c> JSON. See
/// <see cref="RuntimeTemplate.CodeWorkspaceJson"/>.
/// </summary>
public class WorkspaceSettingsSeed
{
    /// <summary>The raw workspace JSON template. Round-tripped verbatim.</summary>
    [TomlPropertyName("json")]
    public string Json { get; set; } = string.Empty;
}

/// <summary>The <c>[template]</c> table — identifying metadata and id ranges.</summary>
public class TemplateMetaSeed
{
    public string Key { get; set; } = string.Empty;

    /// <summary>Runtime version, e.g. <c>"15"</c> or <c>"15.2"</c>. Bare integers normalised before deserialisation.</summary>
    public string Runtime { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int CoreIdRangeFrom { get; set; } = 90000;
    public int CoreIdRangeTo { get; set; } = 90999;
    public int ModuleIdRangeStart { get; set; } = 91000;
    public int ModuleIdRangeSize { get; set; } = 200;

    /// <summary>Optional curated <c>application_versions</c> catalogue key.</summary>
    public string? DefaultApplicationVersion { get; set; }

    /// <summary>Mirrors <c>RuntimeTemplate.IsDefault</c> for export/import round-tripping.</summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Catalogue modules pre-selected on the New Workspace form when this
    /// template is chosen. Lives under <c>[template]</c> rather than
    /// <c>[defaults]</c> because module selection is a workspace-composition
    /// concern, not app.json content.
    /// </summary>
    public List<TemplateDefaultModuleSeed> DefaultModules { get; set; } = new();
}

/// <summary>One <c>[[template.default_modules]]</c> entry.</summary>
public class TemplateDefaultModuleSeed
{
    public string Key { get; set; } = string.Empty;
}

/// <summary>The <c>[defaults]</c> table — verbatim merge into every app.json + form pre-fills.</summary>
public class DefaultsSeed
{
    public string Publisher { get; set; } = string.Empty;
    public string Target { get; set; } = "Cloud";

    /// <summary>Pre-fills the <c>application</c> field on New Workspace.</summary>
    public string Application { get; set; } = string.Empty;

    /// <summary>Pre-fills the <c>platform</c> field on New Workspace.</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>Pre-fills the per-workspace short identifier (e.g. <c>"ACME"</c> → <c>"ACME Core"</c>).</summary>
    [TomlPropertyName("extension_prefix")]
    public string ExtensionPrefix { get; set; } = string.Empty;

    public string? Url { get; set; }
    public string? Logo { get; set; }
    public List<string> Features { get; set; } = new();

    [TomlPropertyName("supportedLocales")]
    public List<string> SupportedLocales { get; set; } = new();

    /// <summary>AL object-name affix substituted into <c>{{affix}}</c> placeholders.</summary>
    public string Affix { get; set; } = string.Empty;

    /// <summary>One of <c>"None"</c>, <c>"Prefix"</c>, or <c>"Suffix"</c>.</summary>
    [TomlPropertyName("affixType")]
    public string AffixType { get; set; } = "None";

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

/// <summary>The <c>[appSourceCop]</c> table — copied verbatim into <c>AppSourceCop.json</c> when <see cref="Include"/> is true; otherwise the file is omitted from generated workspaces.</summary>
public class AppSourceCopSeed
{
    /// <summary>Whether to emit <c>AppSourceCop.json</c>. Defaults to true.</summary>
    [TomlPropertyName("include")]
    public bool Include { get; set; } = true;

    [TomlPropertyName("mandatoryPrefix")]
    public string MandatoryPrefix { get; set; } = string.Empty;

    [TomlPropertyName("supportedCountries")]
    public List<string> SupportedCountries { get; set; } = new();
}

/// <summary>One <c>[[extensions]]</c> entry.</summary>
public class ExtensionSeed
{
    /// <summary>Stable identifier (folder name in the ZIP; reference target for intra-template dependencies).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Mustache template for the rendered extension name (e.g. <c>"{{extension_prefix}} Core"</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>True (default) for always-emitted; false for opt-in extensions.</summary>
    public bool Required { get; set; } = true;

    /// <summary>Optional per-extension override of the template-wide application.</summary>
    public string? Application { get; set; }

    /// <summary>Optional per-extension override of the template-wide runtime.</summary>
    public string? Runtime { get; set; }

    /// <summary>Optional explicit id-range start. When both this and <see cref="IdRangeTo"/> are set, the generator uses them verbatim.</summary>
    [TomlPropertyName("id_range_from")]
    public int? IdRangeFrom { get; set; }

    [TomlPropertyName("id_range_to")]
    public int? IdRangeTo { get; set; }

    /// <summary>Top-level folders under this extension. Nesting is recursive via <see cref="FolderSeed.Folders"/>.</summary>
    public List<FolderSeed> Folders { get; set; } = new();

    public List<DependencySeed> Dependencies { get; set; } = new();
}

/// <summary>One folder in the recursive tree. Files attach at any depth.</summary>
public class FolderSeed
{
    /// <summary>Single path segment (no <c>/</c>).</summary>
    public string Path { get; set; } = string.Empty;

    public List<FolderSeed> Folders { get; set; } = new();
    public List<FolderFileSeed> Files { get; set; } = new();
}

/// <summary>One file attached to a folder.</summary>
public class FolderFileSeed
{
    public string Path { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;

    /// <summary>Ships only when "include examples" is on; otherwise the file is omitted from the ZIP.</summary>
    [TomlPropertyName("is_example")]
    public bool IsExample { get; set; }
}

/// <summary>
/// One <c>[[extensions.dependencies]]</c> entry. Exactly one of
/// <see cref="Extension"/>, <see cref="Module"/>, or <see cref="Id"/> is set.
/// The other reference fields hang off the literal form.
/// </summary>
public class DependencySeed
{
    /// <summary>Intra-template reference: another <c>[[extensions]] path</c>.</summary>
    public string? Extension { get; set; }

    /// <summary>Catalogue reference: a <c>Module.Key</c>.</summary>
    public string? Module { get; set; }

    /// <summary>Literal reference: the AL app GUID. Pairs with <see cref="Name"/>, <see cref="Publisher"/>, <see cref="Version"/>.</summary>
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Publisher { get; set; }
    public string? Version { get; set; }
}
