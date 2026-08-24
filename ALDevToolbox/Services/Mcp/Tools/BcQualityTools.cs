using System.ComponentModel;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.BcQuality;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ALDevToolbox.Services.Mcp.Tools;

/// <summary>
/// MCP tools over the mirrored BCQuality knowledge base — Microsoft's
/// published Business Central quality guidance
/// (https://github.com/microsoft/BCQuality, MIT), ingested into Postgres by
/// <see cref="BcQualityIngestService"/> so an agent gets searchable,
/// version-filtered guidance with no local clone. See
/// <c>.design/bcquality.md</c>.
///
/// <para>
/// Article ids are BCQuality's own citation key — the repo-relative path — so
/// whatever an agent quotes from these tools can be cited straight back at the
/// upstream repository. <see cref="GetArticleAsync"/> returns the commit SHA
/// the mirror is at for the same reason.
/// </para>
///
/// <para>
/// The content is system-level: no organisation scoping, because these rows
/// are the same public guidance for every tenant.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class BcQualityTools
{
    private readonly BcQualitySearchService _search;

    public BcQualityTools(BcQualitySearchService search)
    {
        _search = search;
    }

    [McpServerTool(Name = "search_bcquality", ReadOnly = true)]
    [Description(
        "Searches Microsoft's BCQuality knowledge base — the published Business Central quality guidance "
        + "(performance, security, UI and accessibility, upgrade, events, privacy, telemetry, testing, style, "
        + "AppSource, error handling, interfaces, data modeling, breaking changes, web services, query). "
        + "Use it before writing or reviewing AL to check whether Microsoft has a documented rule for the "
        + "pattern in front of you. Returns ranked hits, each with an Id (the article's repo-relative path), "
        + "Title, Domain, the BcVersion the guidance applies to, Keywords, the article's Description paragraph, "
        + "and a Snippet showing why it matched. Pass the Id to get_bcquality_article for the full rule and its "
        + "AL sample files. Ranking is full-text: a title match outranks a keyword match outranks a body match.")]
    public async Task<IReadOnlyList<BcQualitySearchHit>> SearchAsync(
        [Description("What to search for. Plain words work; \"quoted phrases\" match exactly and a -prefix excludes a term.")] string query,
        [Description("Optional Business Central major version, e.g. 26. Filters to guidance that applies to that version - articles marked for every version, for an explicit list containing it, or for a range that starts at or below it. Take it from the target app's app.json 'application' property. Omit it and no version filtering is applied.")] int? bcVersion = null,
        [Description("Optional domain tag to narrow the search, matching the Domain field on a hit - for example 'performance', 'security', 'ui', 'upgrade', 'testing'. Omit it to search every domain.")] string? domain = null,
        [Description("Maximum hits to return. Defaults to 10, capped at 50.")] int limit = 10,
        CancellationToken ct = default)
    {
        try
        {
            var hits = await _search.SearchAsync(query, bcVersion, domain, limit, ct);
            if (hits.Count == 0 && !await _search.HasContentAsync(ct))
            {
                throw new McpException(
                    "The BCQuality knowledge base has not been mirrored on this server yet. "
                    + "It is fetched by a background refresh shortly after startup; try again in a few minutes, "
                    + "or ask an administrator whether the refresh is disabled.");
            }
            return hits;
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Validation failed: " + FormatErrors(ex.Errors));
        }
    }

    [McpServerTool(Name = "get_bcquality_article", ReadOnly = true)]
    [Description(
        "Returns one BCQuality article in full: its Description, Best Practice and Anti Pattern sections, its "
        + "applicability metadata, and every sample file that ships with it. BCQuality articles never contain "
        + "code inline - the good and bad AL examples are separate files, and they come back here in Samples, "
        + "each with the repo-relative Path to cite it by. CommitSha is the upstream revision this copy was "
        + "read from, so a citation can name an exact version of the guidance. Find ids with search_bcquality.")]
    public async Task<BcQualityArticleDetail> GetArticleAsync(
        [Description("The article's repo-relative path from search_bcquality, for example microsoft/knowledge/ui/no-nested-grids.md.")] string id,
        CancellationToken ct = default)
    {
        try
        {
            return await _search.GetAsync(id, ct)
                ?? throw new McpException(
                    $"No BCQuality article with the id '{id}'. Ids are repo-relative paths such as "
                    + "microsoft/knowledge/performance/apply-filters-before-iterating.md; use search_bcquality to find one.");
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Validation failed: " + FormatErrors(ex.Errors));
        }
    }

    private static string FormatErrors(IReadOnlyDictionary<string, string> errors) =>
        string.Join("; ", errors.Select(kv => $"{kv.Key}: {kv.Value}"));
}
