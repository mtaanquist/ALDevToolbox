using System.Text.Json.Serialization;

namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// The strongly-typed view of <c>defaults_json</c> on a runtime template. These
/// values are the default starting state of a workspace plan: some merge
/// verbatim into every generated <c>app.json</c> (publisher, target, features,
/// supportedLocales, resourceExposurePolicy), some pre-fill the New Workspace
/// form (application, platform, extension_prefix). See
/// <c>.design/templates-and-seeding.md</c> for the split.
/// </summary>
public class TemplateDefaults
{
    [JsonPropertyName("publisher")]
    public string Publisher { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = "Cloud";

    /// <summary>
    /// Pre-fills the <c>application</c> field on the New Workspace form. Moved
    /// off the <c>runtime_templates</c> column of the same name when the
    /// unified-extensions model landed — the field is conceptually app.json
    /// content, not template metadata.
    /// </summary>
    [JsonPropertyName("application")]
    public string Application { get; set; } = string.Empty;

    /// <summary>
    /// Pre-fills the <c>platform</c> field on the New Workspace form. Same
    /// motivation as <see cref="Application"/>.
    /// </summary>
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    /// <summary>
    /// Pre-fills the per-workspace extension-prefix on the New Workspace form
    /// (e.g. <c>CRONUS</c> → renders extension names like <c>CRONUS Core</c>). User
    /// can override per workspace. Distinct from <see cref="Affix"/>: this is
    /// the friendly extension-name prefix, not the AL object-name affix.
    /// </summary>
    [JsonPropertyName("extension_prefix")]
    public string ExtensionPrefix { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("logo")]
    public string? Logo { get; set; }

    [JsonPropertyName("features")]
    public List<string> Features { get; set; } = new();

    [JsonPropertyName("supportedLocales")]
    public List<string> SupportedLocales { get; set; } = new();

    /// <summary>
    /// The AL object-name affix substituted into <c>{{affix}}</c> placeholders
    /// in <c>.al</c> file content during generation. With
    /// <see cref="AffixType"/> set to <see cref="ValueObjects.AffixType.None"/>
    /// the placeholder collapses to the empty string regardless of this value.
    /// </summary>
    [JsonPropertyName("affix")]
    public string Affix { get; set; } = string.Empty;

    [JsonPropertyName("affixType")]
    public AffixType AffixType { get; set; } = AffixType.None;

    [JsonPropertyName("resourceExposurePolicy")]
    public ResourceExposurePolicy ResourceExposurePolicy { get; set; } = new();
}

/// <summary>The <c>resourceExposurePolicy</c> sub-object as defined by AL.</summary>
public class ResourceExposurePolicy
{
    [JsonPropertyName("allowDebugging")]
    public bool AllowDebugging { get; set; }

    [JsonPropertyName("allowDownloadingSource")]
    public bool AllowDownloadingSource { get; set; }

    [JsonPropertyName("includeSourceInSymbolFile")]
    public bool IncludeSourceInSymbolFile { get; set; }
}
