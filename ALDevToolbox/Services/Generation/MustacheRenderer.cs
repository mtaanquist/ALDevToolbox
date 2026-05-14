using System.Text.RegularExpressions;

namespace ALDevToolbox.Services.Generation;

/// <summary>
/// Renders the small set of <c>{{name}}</c>-style placeholders the generator
/// supports against a <see cref="MustacheContext"/>. Pure: no DB access, no
/// org-context dependency. Decoupled from <see cref="GenerationService"/> so
/// the substitution table can be unit-tested in isolation (#86).
/// </summary>
/// <remarks>
/// Supported variables: <c>{{name}}</c>, <c>{{workspaceName}}</c>,
/// <c>{{shortName}}</c>, <c>{{moduleName}}</c>, <c>{{publisher}}</c>,
/// <c>{{extension_prefix}}</c>, <c>{{affix}}</c>, <c>{{namespace}}</c>,
/// <c>{{guid}}</c>. Unknown keys log a warning and pass through verbatim.
/// </remarks>
public sealed class MustacheRenderer
{
    private static readonly Regex MustacheRegex = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    private readonly ILogger<MustacheRenderer> _logger;

    public MustacheRenderer(ILogger<MustacheRenderer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Substitutes every supported placeholder in <paramref name="source"/>.
    /// Unknown variables are left as-is and logged at Warning.
    /// </summary>
    public string Render(string source, MustacheContext ctx) =>
        MustacheRegex.Replace(source, match =>
        {
            var key = match.Groups[1].Value;
            return key switch
            {
                "name" => ctx.Name,
                "workspaceName" => ctx.WorkspaceName,
                "shortName" => ctx.ShortName,
                "moduleName" => ctx.ModuleName,
                "publisher" => ctx.Publisher,
                "extension_prefix" => ctx.ExtensionPrefix,
                "affix" => ctx.Affix,
                "namespace" => ctx.FolderPath.Replace('/', '.'),
                "guid" => Guid.NewGuid().ToString(),
                _ => UnknownVariable(match.Value, key),
            };
        });

    private string UnknownVariable(string original, string key)
    {
        _logger.LogWarning("Unknown mustache variable {{{{{Key}}}}} encountered during generation; left as-is.", key);
        return original;
    }
}

/// <summary>
/// Bag of values consumed by <see cref="MustacheRenderer.Render"/>. Built per
/// extension or per file by the orchestrator so the same renderer can serve
/// every emit point with different substitution context.
/// </summary>
public record MustacheContext(
    string Name,
    string WorkspaceName,
    string ShortName,
    string ModuleName,
    string Publisher,
    string ExtensionPrefix,
    string Affix,
    string FolderPath);
