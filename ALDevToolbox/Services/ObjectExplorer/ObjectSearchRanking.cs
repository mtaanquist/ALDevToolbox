using System.Linq.Expressions;
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

            // Quoted run → exact match: the whole object name, or (for a
            // numeric) the exact ObjectId. Stricter than the substring default
            // so `"36"` / `"Sales Header"` return only exact hits. Exact is
            // all-or-nothing, so it never contributes a ranking token.
            if (token.Quoted)
            {
                q = int.TryParse(text, out var exactId)
                    ? ApplyPredicate(q, token.Negated, o => o.ObjectId == exactId || o.Name.ToLower() == text)
                    : ApplyPredicate(q, token.Negated, o => o.Name.ToLower() == text);
                continue;
            }

            // `lo..hi` → inclusive ObjectId range (e.g. 50000..99999).
            if (TryParseIdRange(text, out var lo, out var hi))
            {
                q = ApplyPredicate(q, token.Negated,
                    o => o.ObjectId != null && o.ObjectId >= lo && o.ObjectId <= hi);
                continue;
            }

            // `sales*` / `*sales` / `*sales*` → anchored glob on the name.
            var (globKind, needle) = ParseGlob(text);
            if (globKind != GlobKind.None)
            {
                if (needle.Length == 0) continue; // a bare `*` matches everything
                q = globKind switch
                {
                    GlobKind.Prefix => ApplyPredicate(q, token.Negated, o => o.Name.ToLower().StartsWith(needle)),
                    GlobKind.Suffix => ApplyPredicate(q, token.Negated, o => o.Name.ToLower().EndsWith(needle)),
                    _               => ApplyPredicate(q, token.Negated, o => o.Name.ToLower().Contains(needle)),
                };
                if (!token.Negated) rankTokens.Add(needle);
                continue;
            }

            // Default: case-insensitive substring on the name.
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
    /// Wraps <paramref name="predicate"/> in a <c>Where</c>, negating it (via
    /// <see cref="Expression.Not"/>) when <paramref name="negated"/> is set, so
    /// a leading <c>-</c> works uniformly across exact / range / glob tokens.
    /// </summary>
    private static IQueryable<ModuleObject> ApplyPredicate(
        IQueryable<ModuleObject> q, bool negated, Expression<Func<ModuleObject, bool>> predicate)
    {
        if (!negated) return q.Where(predicate);
        var not = Expression.Lambda<Func<ModuleObject, bool>>(
            Expression.Not(predicate.Body), predicate.Parameters);
        return q.Where(not);
    }

    /// <summary>
    /// Parses an inclusive id-range token of the form <c>lo..hi</c> (e.g.
    /// <c>50000..99999</c>). Reversed bounds are tolerated. Returns false for
    /// anything that isn't exactly two integers around a single <c>..</c>
    /// (so open-ended <c>50000..</c> / <c>..99999</c> stay plain tokens).
    /// </summary>
    internal static bool TryParseIdRange(string text, out int lo, out int hi)
    {
        lo = 0;
        hi = 0;
        var dots = text.IndexOf("..", StringComparison.Ordinal);
        if (dots <= 0 || dots + 2 >= text.Length) return false;
        if (text.IndexOf("..", dots + 2, StringComparison.Ordinal) >= 0) return false; // only one ".."
        if (!int.TryParse(text[..dots], out lo) || !int.TryParse(text[(dots + 2)..], out hi))
            return false;
        if (lo > hi) (lo, hi) = (hi, lo);
        return true;
    }

    internal enum GlobKind { None, Prefix, Suffix, Contains }

    /// <summary>
    /// Classifies a <c>*</c>-bearing token into an anchored glob: <c>foo*</c>
    /// is a prefix match, <c>*foo</c> a suffix, <c>*foo*</c> (or an internal
    /// <c>*</c>) a contains. A token with no <c>*</c> returns
    /// <see cref="GlobKind.None"/> so the caller keeps the substring default.
    /// The needle is the token with its <c>*</c>s stripped.
    /// </summary>
    internal static (GlobKind Kind, string Needle) ParseGlob(string text)
    {
        if (!text.Contains('*')) return (GlobKind.None, text);
        var starts = text.StartsWith('*');
        var ends = text.EndsWith('*');
        var core = text.Replace("*", "");
        if (starts && ends) return (GlobKind.Contains, core);
        if (ends) return (GlobKind.Prefix, core);
        if (starts) return (GlobKind.Suffix, core);
        return (GlobKind.Contains, core); // internal '*': fall back to contains
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
