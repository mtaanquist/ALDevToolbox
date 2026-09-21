using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.Tools;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Palette.Sources;

/// <summary>
/// Cookbook recipes, for the developer who half remembers what a recipe does
/// but not what it is called. See <c>.design/command-palette.md</c>, "Sources".
///
/// <para><b>Title and tags, never the summary or the files.</b> A palette row is
/// a title and one line under it, so a row that matched on a sentence the reader
/// cannot see reads as a bug - which is why the Cookbook's own search (which
/// also reads the description) is deliberately not mirrored here, and why the
/// tag shown in the subtitle is the tag that matched when one did.</para>
///
/// <para><b>The fence.</b> Recipes are org-scoped and hang off no solution, so
/// the whole of the visibility rule is the EF query filter plus the two flags
/// the Cookbook list already applies: no soft-deleted rows, no deprecated ones.
/// No <c>IgnoreQueryFilters()</c>, and no second read - the set this returns is
/// the set <c>/cookbook</c> shows the same caller.</para>
/// </summary>
public sealed class RecipePaletteSource : IPaletteSource
{
    /// <summary>
    /// How many times the asked-for limit is read from the database. The
    /// <c>ILIKE</c> pre-filter is looser than the ranking that follows it - it
    /// matches a term in any tag, while ranking only sees the one tag the
    /// subtitle carries - so reading exactly <paramref name="limit"/> rows would
    /// let dropped rows eat the group. See <see cref="PaletteQuery.SqlTerms"/>.
    /// </summary>
    private const int OverReadFactor = 4;

    private readonly AppDbContext _db;
    private readonly IToolAvailability _tools;

    public RecipePaletteSource(AppDbContext db, IToolAvailability tools)
    {
        _db = db;
        _tools = tools;
    }

    public string Id => "recipes";

    public string Label => "Recipes";

    public int Order => PaletteGroupOrder.Recipes;

    /// <summary>
    /// Exactly the Cookbook's own gate: any signed-in member of the
    /// organisation, unless a SiteAdmin has switched the tool off site-wide or
    /// the caller's organisation has switched it off for itself. Same two checks
    /// <c>NavMenu</c> makes before drawing the Cookbook link, so the palette
    /// never offers a page the nav would not.
    /// </summary>
    public Task<bool> IsAvailableAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        var available = user.Identity?.IsAuthenticated == true
            && _tools.IsSiteEnabled(ToolKey.Cookbook)
            && !EndpointHelpers.ReadDisabledTools(user).Contains(ToolKey.Cookbook);
        return Task.FromResult(available);
    }

    public async Task<IReadOnlyList<PaletteCandidate>> SearchAsync(
        PaletteQuery query, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.IsUsable) return [];

        // The Cookbook table is the one place in the palette where a per-term
        // ILIKE earns its keep: an organisation's recipes outnumber its
        // solutions and the caller is usually typing a word out of the title.
        // Accent-sensitive, deliberately - see PaletteQuery.SqlTerms.
        var rows = _db.Recipes
            .AsNoTracking()
            .Where(r => r.DeletedAt == null && !r.Deprecated);

        foreach (var term in query.SqlTerms)
        {
            // Escaped so a literal % or _ in the term matches itself rather
            // than everything, paired with the explicit escape character (#385).
            var pattern = "%" + ObjectSearchService.EscapeLike(term) + "%";
            rows = rows.Where(r =>
                EF.Functions.ILike(r.Title, pattern, "\\")
                || EF.Functions.ILike(r.Keywords, pattern, "\\"));
        }

        var found = await rows
            .OrderBy(r => r.Title)
            .Take(limit * OverReadFactor)
            .Select(r => new
            {
                r.Id,
                r.Title,
                r.Keywords,
                MinimumVersion = r.MinimumApplicationVersion == null
                    ? null
                    : r.MinimumApplicationVersion.Name,
            })
            .ToListAsync(ct);

        return found
            .Select(r => new PaletteCandidate(
                "recipe", r.Title, Subtitle(query, r.MinimumVersion, r.Keywords), $"/cookbook/{r.Id}"))
            .ToList();
    }

    /// <summary>
    /// The line under the title, in the Cookbook list's own words: the minimum
    /// application version ("... or later") and one tag.
    ///
    /// <para>Which tag is the interesting part. The list shows the first few; a
    /// palette row has space for one, and ranking only matches what a row
    /// actually shows - so the tag on offer is the first one a typed term
    /// matched, falling back to the recipe's first tag when the match came from
    /// the title. Without that, a recipe found by its third tag would be dropped
    /// by ranking, and one that survived would be a row whose reason for being
    /// there is invisible.</para>
    /// </summary>
    private static string? Subtitle(PaletteQuery query, string? minimumVersion, string? keywords)
    {
        var tags = (keywords ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string? tag = tags.Length > 0 ? tags[0] : null;
        foreach (var candidate in tags)
        {
            var folded = PaletteQuery.Fold(candidate);
            if (query.Terms.Any(term => folded.Contains(term, StringComparison.Ordinal)))
            {
                tag = candidate;
                break;
            }
        }

        var version = string.IsNullOrWhiteSpace(minimumVersion) ? null : minimumVersion + " or later";
        return (version, tag) switch
        {
            (null, null) => null,
            (null, { } t) => t,
            ({ } v, null) => v,
            var (v, t) => v + " - " + t,
        };
    }
}
