using System.Text.Json.Serialization;

namespace AlWorkspaceBuilder.Domain.ValueObjects;

/// <summary>
/// The strongly-typed view of <c>app_source_cop_json</c> on a runtime template.
/// These values are written verbatim into the generated <c>AppSourceCop.json</c>.
/// </summary>
public class AppSourceCopSettings
{
    [JsonPropertyName("mandatoryPrefix")]
    public string MandatoryPrefix { get; set; } = string.Empty;

    [JsonPropertyName("supportedCountries")]
    public List<string> SupportedCountries { get; set; } = new();
}
