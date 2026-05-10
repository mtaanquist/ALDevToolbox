using System.Text.Json.Serialization;

namespace ALDevToolbox.Domain.ValueObjects;

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
