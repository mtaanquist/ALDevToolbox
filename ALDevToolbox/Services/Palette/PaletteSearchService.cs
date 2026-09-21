using System.Diagnostics;
using System.Security.Claims;

namespace ALDevToolbox.Services.Palette;

/// <summary>
/// Fans one query out across the registered <see cref="IPaletteSource"/>s the
/// caller may use, ranks what comes back, caps it, and hands the browser the
/// shape in <see cref="PaletteSearchResult"/>. See
/// <c>.design/command-palette.md</c>.
///
/// <para><b>Sources run one after another, never in parallel.</b> They share the
/// request's <c>DbContext</c>, which allows one operation at a time; a
/// <c>Task.WhenAll</c> here would throw at runtime under exactly the load the
/// palette produces. The budget the design sets (under 100 ms for an
/// organisation with a few hundred solutions) is met by each source being one
/// limited, projected read — not by running them at once.</para>
///
/// <para>Registered concrete and scoped, like every other service here. With no
/// sources registered it answers <see cref="PaletteSearchResult.Empty"/>,
/// which is a working palette with nothing in it rather than an error.</para>
/// </summary>
public sealed class PaletteSearchService
{
    /// <summary>Rows per group, once more than one group has matched.</summary>
    public const int MaxPerGroup = 5;

    /// <summary>
    /// Rows when exactly one group matched. Nothing is competing for the space,
    /// so the one group that answered gets more of it.
    /// </summary>
    public const int MaxWhenOnlyOneGroupMatches = 8;

    /// <summary>
    /// What each source is asked for. Deliberately several times the cap:
    /// ranking drops every candidate that does not match every term, and the top
    /// hit is lifted out of its group, so a source that returned exactly
    /// <see cref="MaxPerGroup"/> rows could end up showing four.
    /// </summary>
    public const int CandidatesPerSource = 24;

    private readonly IReadOnlyList<IPaletteSource> _sources;
    private readonly ILogger<PaletteSearchService> _logger;

    public PaletteSearchService(IEnumerable<IPaletteSource> sources, ILogger<PaletteSearchService> logger)
    {
        ArgumentNullException.ThrowIfNull(sources);
        // Sorted once, here, so the registration order in Startup/ is never what
        // decides the order a user sees. Id breaks a tie so the order is total.
        _sources = sources
            .OrderBy(s => s.Order)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
        _logger = logger;
    }

    /// <summary>
    /// Searches every source <paramref name="user"/> passes the gate for.
    ///
    /// <para>A query shorter than <see cref="PaletteQuery.MinLength"/> or longer
    /// than <see cref="PaletteQuery.MaxLength"/> comes back empty without a
    /// single source being asked — so a keystroke that cannot usefully match
    /// costs no database work at all.</para>
    /// </summary>
    public async Task<PaletteSearchResult> SearchAsync(
        ClaimsPrincipal user, string? rawQuery, CancellationToken ct)
    {
        var query = PaletteQuery.Parse(rawQuery);
        if (!query.IsUsable) return PaletteSearchResult.Empty;

        var startedAt = Stopwatch.GetTimestamp();
        var matchesBySource = new List<(IPaletteSource Source, List<PaletteMatch> Matches)>(_sources.Count);
        var asked = 0;

        foreach (var source in _sources)
        {
            ct.ThrowIfCancellationRequested();
            if (!await source.IsAvailableAsync(user, ct).ConfigureAwait(false)) continue;

            asked++;
            var candidates = await source.SearchAsync(query, CandidatesPerSource, ct).ConfigureAwait(false);
            var matches = PaletteRanking.Rank(query, candidates ?? []);
            if (matches.Count > 0) matchesBySource.Add((source, matches));
        }

        // The top hit: an exact short-name match, lifted out of its group so it
        // is not shown twice. Groups are already in display order and each
        // group's matches are sorted best-first, so the first group holding one
        // holds it at index 0 — but find it by tier rather than by position, so
        // a change to the sort can't quietly turn this into "the first row".
        PaletteResultItem? top = null;
        foreach (var (_, matches) in matchesBySource)
        {
            var index = matches.FindIndex(static m => m.Tier == PaletteMatchTier.ExactShortName);
            if (index < 0) continue;
            top = PaletteResultItem.From(matches[index].Candidate);
            matches.RemoveAt(index);
            break;
        }

        var nonEmpty = matchesBySource.Where(entry => entry.Matches.Count > 0).ToList();
        var cap = nonEmpty.Count == 1 ? MaxWhenOnlyOneGroupMatches : MaxPerGroup;

        var groups = nonEmpty
            .Select(entry => new PaletteResultGroup(
                entry.Source.Id,
                entry.Source.Label,
                entry.Matches.Take(cap).Select(m => PaletteResultItem.From(m.Candidate)).ToList()))
            .ToList();

        var shown = groups.Sum(g => g.Items.Count) + (top is null ? 0 : 1);
        // Debug, not Information: this fires once per debounced keystroke, so at
        // Information it would be most of the container's log. And never the
        // query text above Debug: it is a customer name somebody typed.
        _logger.LogDebug(
            "Palette search asked {SourceCount} of {RegisteredCount} sources and returned {ResultCount} rows in {GroupCount} groups (top hit: {HasTop}) in {ElapsedMs} ms",
            asked, _sources.Count, shown, groups.Count, top is not null,
            (int)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        _logger.LogDebug("Palette search for {Query} matched {Groups}",
            query.Raw, string.Join(", ", groups.Select(g => $"{g.Id}={g.Items.Count}")));

        return groups.Count == 0 && top is null
            ? PaletteSearchResult.Empty
            : new PaletteSearchResult(groups, top);
    }
}
