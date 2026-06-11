using System.Text.RegularExpressions;

namespace ALDevToolbox.Services.Generation;

/// <summary>
/// Naming helpers shared by the generation pipeline. The workspace/extension
/// "short name" (folder names, the <c>.code-workspace</c> file name) is the
/// display name with all whitespace removed; both <c>GenerationService</c> and
/// <c>WorkspaceZipBuilder</c> derive it, so the rule lives here once.
/// </summary>
public static partial class GenerationNaming
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    public static string StripWhitespace(string value) =>
        WhitespaceRegex().Replace(value ?? string.Empty, string.Empty);

    /// <summary>
    /// Resolves the publisher that <c>{{publisher}}</c> renders to in the
    /// workspace flow. The canonical value is the org's configuration default
    /// (<see cref="Domain.Entities.OrganizationSettings.DefaultPublisher"/>),
    /// set at <c>/admin/configuration/defaults</c> — the same source the
    /// standalone New Extension flow uses. The template's own
    /// <c>defaults_json.publisher</c> is only a fallback for a fresh org whose
    /// settings row is still blank, so a populated org setting always wins.
    /// Both generation paths read this so the two stay in lock-step.
    /// </summary>
    public static string ResolvePublisher(string? orgDefaultPublisher, string? templateDefaultPublisher) =>
        !string.IsNullOrWhiteSpace(orgDefaultPublisher)
            ? orgDefaultPublisher
            : templateDefaultPublisher ?? string.Empty;
}
