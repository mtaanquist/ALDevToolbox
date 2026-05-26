using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Search-box tokenizing and result ranking for cross-module object search,
/// extracted from <see cref="ObjectExplorerService"/> so the parsing/ranking
/// rules can be read and tested on their own. Pure aside from the bounded
/// <see cref="ExecuteAndRankAsync"/> materialisation, which runs the supplied
/// EF query and re-orders the page in memory.
/// </summary>
internal static class ObjectSearchRanking
{
    private readonly record struct SearchToken(string Text, bool Negated, bool Quoted);

    /// <summary>
    /// Trims, lower-cases, and de-duplicates a kind filter list, collapsing an
    /// empty/all-blank list to null so callers can treat "no kinds" uniformly.
    /// </summary>
    public static IReadOnlyList<string>? NormalizeKinds(IReadOnlyList<string>? kinds)
    {
        if (kinds is null) return null;
        var result = kinds
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// Applies the parsed search tokens to <paramref name="q"/> as
    /// case-insensitive name filters (a leading <c>-</c> negates), with one
    /// legacy special case: a single bare numeric token also matches by
    /// <c>ObjectId</c>. Returns the positive token texts so the caller can
    /// rank matches; negated tokens only filter and never contribute a score.
    /// </summary>
    public static (IQueryable<ModuleObject> Query, IReadOnlyList<string> Tokens)
        ApplySearchTokens(IQueryable<ModuleObject> q, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return (q, Array.Empty<string>());

        var tokens = TokenizeSearch(search);
        if (tokens.Count == 0)
            return (q, Array.Empty<string>());

        if (tokens.Count == 1 && tokens[0] is { Negated: false, Quoted: false } single
            && int.TryParse(single.Text, out var asInt))
        {
            var lower = single.Text;
            q = q.Where(o => o.ObjectId == asInt || o.Name.ToLower().Contains(lower));
            // Numeric path: ranking by name tokens would be misleading
            // because the id branch matched without a name hit.
            return (q, Array.Empty<string>());
        }

        var rankTokens = new List<string>();
        foreach (var token in tokens)
        {
            var text = token.Text;
            if (token.Negated)
            {
                q = q.Where(o => !o.Name.ToLower().Contains(text));
            }
            else
            {
                q = q.Where(o => o.Name.ToLower().Contains(text));
                rankTokens.Add(text);
            }
        }
        return (q, rankTokens);
    }

    /// <summary>
    /// Hand-rolled tokenizer for the search box. Walks the string once,
    /// honouring an optional leading <c>-</c> (negation) and double-quoted
    /// runs (a literal phrase, spaces and all). Tokens are lower-cased for
    /// case-insensitive matching; empty tokens (a lone <c>-</c> or <c>""</c>)
    /// are dropped.
    /// </summary>
    private static List<SearchToken> TokenizeSearch(string search)
    {
        var tokens = new List<SearchToken>();
        var i = 0;
        var n = search.Length;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(search[i])) i++;
            if (i >= n) break;

            var negated = search[i] == '-';
            if (negated) i++;

            string text;
            bool quoted;
            if (i < n && search[i] == '"')
            {
                quoted = true;
                i++; // opening quote
                var start = i;
                while (i < n && search[i] != '"') i++;
                text = search[start..i];
                if (i < n) i++; // closing quote
            }
            else
            {
                quoted = false;
                var start = i;
                while (i < n && !char.IsWhiteSpace(search[i])) i++;
                text = search[start..i];
            }

            if (text.Length == 0) continue;
            tokens.Add(new SearchToken(text.ToLowerInvariant(), negated, quoted));
        }
        return tokens;
    }

    /// <summary>
    /// Materialises the filtered query under the legacy (Kind, ObjectId,
    /// Name) DB order — that order remains the result contract when no
    /// search is supplied — and, when search tokens are present, re-ranks
    /// the page in memory so word-boundary hits float above mid-word hits.
    /// The in-memory pass is bounded by <paramref name="take"/>, so the
    /// extra work is small even on releases with thousands of objects.
    /// </summary>
    public static async Task<List<ReleaseObjectMatch>> ExecuteAndRankAsync(
        IQueryable<ModuleObject> q,
        IReadOnlyList<string> tokens,
        int take,
        CancellationToken ct)
    {
        var rows = await q
            .OrderBy(o => o.Kind).ThenBy(o => o.ObjectId).ThenBy(o => o.Name)
            .Take(take)
            .Select(o => new ReleaseObjectMatch(
                o.Id, o.Kind, o.ObjectId, o.Name, o.Namespace,
                o.ModuleId, o.Module!.Name,
                o.SourceFileId, o.LineNumber,
                o.SourceFile != null ? o.SourceFile.LineCount : 0))
            .ToListAsync(ct);

        if (tokens.Count == 0)
            return rows;

        return rows
            .Select(r => (Row: r, Score: ScoreNameMatch(r.Name, tokens)))
            .OrderByDescending(x => x.Score.BoundaryHits)
            .ThenByDescending(x => x.Score.Earliness)
            .ThenBy(x => x.Row.Kind)
            .ThenBy(x => x.Row.ObjectId)
            .ThenBy(x => x.Row.Name)
            .Select(x => x.Row)
            .ToList();
    }

    /// <summary>
    /// Scores a candidate name against the parsed search tokens. Returns
    /// (a) the number of tokens whose first occurrence starts on a word
    /// boundary (start of name, after a separator, or at a lower→upper
    /// PascalCase transition), and (b) an "earliness" tally that favours
    /// tokens appearing near the front of the name. Higher is better on
    /// both axes; ties fall through to the Kind/ObjectId/Name tiebreakers.
    /// </summary>
    private static (int BoundaryHits, int Earliness) ScoreNameMatch(
        string name, IReadOnlyList<string> tokens)
    {
        var lower = name.ToLowerInvariant();
        var boundaryHits = 0;
        var earliness = 0;
        foreach (var token in tokens)
        {
            var idx = lower.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0) continue;
            earliness -= idx;
            if (IsWordBoundary(name, idx))
                boundaryHits++;
        }
        return (boundaryHits, earliness);
    }

    private static bool IsWordBoundary(string original, int index)
    {
        if (index == 0) return true;
        if (index >= original.Length) return false;
        var prev = original[index - 1];
        if (prev is ' ' or '.' or '-' or '_' or '&' or '/' or ',' or '(' or ')')
            return true;
        // PascalCase boundary: a lowercase character followed by an
        // uppercase one acts like a word boundary for identifiers such as
        // SalesHeader, where "Header" should rank like a fresh word.
        return char.IsLower(prev) && char.IsUpper(original[index]);
    }
}
