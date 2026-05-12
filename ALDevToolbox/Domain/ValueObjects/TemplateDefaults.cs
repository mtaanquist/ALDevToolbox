using System.Text.Json.Serialization;

namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// Whether a template's <see cref="TemplateDefaults.Affix"/> string is applied
/// before or after AL object names. Drives the conditional emission of the
/// <c>{{prefix}}</c> / <c>{{suffix}}</c> mustache variables at generation
/// time; <c>{{affix}}</c> always emits the value regardless.
/// </summary>
public enum AffixType
{
    Prefix = 0,
    Suffix = 1,
}

/// <summary>
/// The strongly-typed view of <c>defaults_json</c> on a runtime template. These
/// values are merged into every generated <c>app.json</c>. The model is closed
/// (only the fields the AL ecosystem currently knows about); future fields can
/// be added by extending this class and emitting a migration if their default
/// representation changes.
/// </summary>
public class TemplateDefaults
{
    [JsonPropertyName("publisher")]
    public string Publisher { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = "Cloud";

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("logo")]
    public string? Logo { get; set; }

    [JsonPropertyName("features")]
    public List<string> Features { get; set; } = new();

    [JsonPropertyName("supportedLocales")]
    public List<string> SupportedLocales { get; set; } = new();

    [JsonPropertyName("resourceExposurePolicy")]
    public ResourceExposurePolicy ResourceExposurePolicy { get; set; } = new();

    /// <summary>
    /// The string applied to AL object names as a prefix or suffix. Surfaces
    /// via three mustache variables: <c>{{prefix}}</c> (emits when
    /// <see cref="AffixType"/> is Prefix, empty otherwise), <c>{{suffix}}</c>
    /// (emits when Suffix), and <c>{{affix}}</c> (always emits the value).
    /// Migrated from the old <c>app_source_cop_json.mandatoryPrefix</c> when
    /// the column was retired; new templates set it directly in
    /// <c>[defaults]</c>.
    /// </summary>
    [JsonPropertyName("affix")]
    public string Affix { get; set; } = string.Empty;

    /// <summary>
    /// Position of <see cref="Affix"/> on generated object names. Defaults to
    /// <c>Prefix</c> so the migration from <c>mandatoryPrefix</c> preserves
    /// behaviour. Serialised as a JSON string ("Prefix" / "Suffix").
    /// </summary>
    [JsonPropertyName("affixType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AffixType AffixType { get; set; } = AffixType.Prefix;
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
