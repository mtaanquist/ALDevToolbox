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
}
