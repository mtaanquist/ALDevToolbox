using System.Text.RegularExpressions;

namespace ALDevToolbox.Services.Generation;

/// <summary>
/// Renders the small set of <c>{{name}}</c>-style placeholders the generator
/// supports against a <see cref="MustacheContext"/>. Pure: no DB access, no
/// org-context dependency. Decoupled from <see cref="GenerationService"/> so
/// the substitution table can be unit-tested in isolation (#86).
/// </summary>
/// <remarks>
/// Canonical names are snake_case to match the TOML schema. The old
/// camelCase names (<c>workspaceName</c>, <c>shortName</c>, <c>moduleName</c>)
/// are still resolved as aliases for backwards-compatibility — a single
/// warning per render lists any deprecated names encountered so admins can
/// rename them in their org files.
/// Supported variables: <c>{{name}}</c>, <c>{{workspace_name}}</c>,
/// <c>{{short_name}}</c>, <c>{{module_name}}</c>, <c>{{publisher}}</c>,
/// <c>{{extension_prefix}}</c>, <c>{{affix}}</c>, <c>{{namespace}}</c>,
/// <c>{{guid}}</c>, <c>{{tenant_id}}</c>.
/// </remarks>
public sealed class MustacheRenderer
{
    private static readonly Regex MustacheRegex = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    /// <summary>
    /// Legacy camelCase → canonical snake_case map. Kept here (rather than in
    /// <see cref="Domain.ValueObjects.MustacheVariableCatalog"/>) because it
    /// is purely a renderer concern: the catalogue describes the placeholders
    /// admins should be authoring against today, not the historical names.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workspaceName"] = "workspace_name",
            ["shortName"] = "short_name",
            ["moduleName"] = "module_name",
        };

    private readonly ILogger<MustacheRenderer> _logger;

    public MustacheRenderer(ILogger<MustacheRenderer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Substitutes every supported placeholder in <paramref name="source"/>.
    /// Unknown variables are left as-is and logged at Warning. Deprecated
    /// camelCase names are resolved silently but accumulated; a single
    /// warning is logged per <see cref="Render"/> call listing them all.
    /// </summary>
    public string Render(string source, MustacheContext ctx)
    {
        HashSet<string>? deprecated = null;
        var result = MustacheRegex.Replace(source, match =>
        {
            var key = match.Groups[1].Value;
            if (Aliases.TryGetValue(key, out var canonical))
            {
                (deprecated ??= new(StringComparer.Ordinal)).Add(key);
                key = canonical;
            }
            return key switch
            {
                "name" => ctx.Name,
                "workspace_name" => ctx.WorkspaceName,
                "short_name" => ctx.ShortName,
                "module_name" => ctx.ModuleName,
                "publisher" => ctx.Publisher,
                "extension_prefix" => ctx.ExtensionPrefix,
                "affix" => ctx.Affix,
                "namespace" => ctx.FolderPath.Replace('/', '.'),
                "guid" => Guid.NewGuid().ToString(),
                "tenant_id" => ctx.TenantId,
                _ => UnknownVariable(match.Value, key),
            };
        });
        if (deprecated is { Count: > 0 })
        {
            _logger.LogWarning(
                "Deprecated camelCase mustache placeholders encountered during generation: {Names}. " +
                "Rename them to their snake_case equivalents.",
                string.Join(", ", deprecated.OrderBy(n => n, StringComparer.Ordinal)));
        }
        return result;
    }

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
/// <remarks>
/// <see cref="TenantId"/> is captured on the New Workspace form and persisted
/// to <c>workspace.aldt.toml</c> so regeneration is reproducible. The
/// standalone extension flow leaves it empty.
/// </remarks>
public record MustacheContext(
    string Name,
    string WorkspaceName,
    string ShortName,
    string ModuleName,
    string Publisher,
    string ExtensionPrefix,
    string Affix,
    string FolderPath,
    string TenantId = "");
