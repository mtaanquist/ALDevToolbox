namespace ALDevToolbox.Services.Palette;

/// <summary>
/// How well a candidate matched, best first. See
/// <c>.design/command-palette.md</c>, "Matching and ranking" — the four tiers
/// there are these four, in this order.
/// </summary>
public enum PaletteMatchTier
{
    /// <summary>
    /// The whole query <em>is</em> the row's short name. The one tier that
    /// escapes its group: this row alone is lifted above every group as the
    /// palette's top hit.
    /// </summary>
    ExactShortName = 0,

    /// <summary>The whole query is a prefix of the title or of the short name.</summary>
    QueryPrefix = 1,

    /// <summary>Every term matches at the start of a word in some searched field.</summary>
    WordStart = 2,

    /// <summary>Every term matches somewhere in some searched field.</summary>
    Substring = 3,
}

/// <summary>A candidate that matched, and how well.</summary>
public readonly record struct PaletteMatch(PaletteCandidate Candidate, PaletteMatchTier Tier);

/// <summary>
/// The one place a palette result's rank is decided. Pure and static: sources
/// hand back candidates, this says which of them matched and how well, so
/// ranking cannot drift from source to source.
///
/// <para>There is deliberately <b>no typo tolerance</b>. Fuzzy matching on
/// customer names produces hits nobody can explain, and it cannot be an indexed
/// query. See <c>.design/command-palette.md</c>, "Deliberately left out".</para>
/// </summary>
public static class PaletteRanking
{
    /// <summary>
    /// The tier <paramref name="candidate"/> matched at, or <c>null</c> when it
    /// did not match — which a source is free to let happen, since over-returning
    /// loosely filtered rows is cheaper than missing one.
    ///
    /// <para>The rule underneath every tier is the same: <b>every term must match
    /// somewhere</b> across the candidate's title, short name, subtitle and
    /// searched-only text, in any order. <c>con cof</c> and <c>cof con</c> both
    /// find "Contoso Coffee"; <c>con prod</c> finds the environment titled
    /// "Production" whose subtitle is "Contoso Coffee".</para>
    ///
    /// <para>Only the two tiers below care <em>which</em> field matched: the top
    /// hit is an exact short name and the prefix tier is over the title and the
    /// short name, so a row found through
    /// <see cref="PaletteCandidate.SearchOnly"/> can never be lifted above its
    /// group by text the user cannot see.</para>
    /// </summary>
    public static PaletteMatchTier? Match(PaletteQuery query, PaletteCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidate);
        if (!query.IsUsable) return null;

        var title = PaletteQuery.Fold(candidate.Title);
        var shortName = PaletteQuery.Fold(candidate.ShortName);
        var subtitle = PaletteQuery.Fold(candidate.Subtitle);
        var searchOnly = PaletteQuery.Fold(candidate.SearchOnly);

        // Ordered so the field a user most likely typed is checked first; the
        // answer does not depend on the order, only the work does.
        Span<string> fields = [title, shortName, subtitle, searchOnly];

        foreach (var term in query.Terms)
        {
            if (!MatchesAnywhere(fields, term)) return null;
        }

        if (shortName.Length > 0 && string.Equals(shortName, query.Folded, StringComparison.Ordinal))
        {
            return PaletteMatchTier.ExactShortName;
        }

        if (title.StartsWith(query.Folded, StringComparison.Ordinal)
            || (shortName.Length > 0 && shortName.StartsWith(query.Folded, StringComparison.Ordinal)))
        {
            return PaletteMatchTier.QueryPrefix;
        }

        foreach (var term in query.Terms)
        {
            if (!MatchesAtWordStart(fields, term)) return PaletteMatchTier.Substring;
        }

        return PaletteMatchTier.WordStart;
    }

    /// <summary>
    /// The candidates that matched, best tier first and ties broken by title
    /// (ordinal, ignoring case) so the same query always produces the same order.
    /// Non-matching candidates are dropped.
    ///
    /// <para>Ties break by <b>name</b>, not by recency: a list that reorders
    /// itself between two identical queries is a list the user cannot learn. See
    /// <c>.design/command-palette.md</c>.</para>
    /// </summary>
    public static List<PaletteMatch> Rank(PaletteQuery query, IEnumerable<PaletteCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);

        var matches = new List<PaletteMatch>();
        foreach (var candidate in candidates)
        {
            if (Match(query, candidate) is { } tier) matches.Add(new PaletteMatch(candidate, tier));
        }

        matches.Sort(static (left, right) =>
        {
            var byTier = left.Tier.CompareTo(right.Tier);
            if (byTier != 0) return byTier;
            var byTitle = string.Compare(
                left.Candidate.Title, right.Candidate.Title, StringComparison.OrdinalIgnoreCase);
            if (byTitle != 0) return byTitle;
            // Two rows with the same title in the same tier still have to order
            // deterministically, or the cap would pick between them at random.
            return string.Compare(left.Candidate.Href, right.Candidate.Href, StringComparison.Ordinal);
        });

        return matches;
    }

    private static bool MatchesAnywhere(ReadOnlySpan<string> fields, string term)
    {
        foreach (var field in fields)
        {
            if (field.Length > 0 && field.Contains(term, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="term"/> starts a word in any field — at index 0
    /// or after something that isn't a letter or a digit, so "coffee" starts a
    /// word in "Contoso Coffee" and in "Contoso-Coffee", but "offee" does not.
    /// </summary>
    private static bool MatchesAtWordStart(ReadOnlySpan<string> fields, string term)
    {
        foreach (var field in fields)
        {
            if (field.Length == 0) continue;
            var index = field.IndexOf(term, StringComparison.Ordinal);
            while (index >= 0)
            {
                if (index == 0 || !char.IsLetterOrDigit(field[index - 1])) return true;
                index = field.IndexOf(term, index + 1, StringComparison.Ordinal);
            }
        }
        return false;
    }
}
