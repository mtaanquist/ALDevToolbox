using System.Text.Json.Serialization;

namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// The strongly-typed view of <c>app_source_cop_json</c> on a runtime template.
/// When <see cref="Include"/> is true the remaining fields are written verbatim
/// into the generated <c>AppSourceCop.json</c>; when false the file is omitted
/// from the ZIP entirely. <see cref="Include"/> is persisted on the row but
/// stripped before the file is emitted — it's our authoring flag, not an AL
/// concept.
/// </summary>
public class AppSourceCopSettings
{
    /// <summary>
    /// Whether to emit <c>AppSourceCop.json</c> into each extension's folder.
    /// Defaults to true so existing templates keep their pre-#59 behaviour.
    /// </summary>
    [JsonPropertyName("include")]
    public bool Include { get; set; } = true;

    [JsonPropertyName("mandatoryPrefix")]
    public string MandatoryPrefix { get; set; } = string.Empty;

    [JsonPropertyName("supportedCountries")]
    public List<string> SupportedCountries { get; set; } = new();
}
