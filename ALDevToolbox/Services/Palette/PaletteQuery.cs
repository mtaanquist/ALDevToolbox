using System.Globalization;
using System.Text;

namespace ALDevToolbox.Services.Palette;

/// <summary>
/// What the user typed into the command palette, split into the terms every
/// source and <see cref="PaletteRanking"/> match against. See
/// <c>.design/command-palette.md</c>, "Matching and ranking".
///
/// <para>The query is split on whitespace and <b>every term must match
/// somewhere</b> in a candidate's searched fields, in any order — so
/// <c>con cof</c> finds "Contoso Coffee" and so does <c>cof con</c>. Matching
/// ignores case and accents: <c>moller</c> finds "Møller".</para>
/// </summary>
public sealed record PaletteQuery
{
    /// <summary>
    /// Shorter than this and the palette does not search at all: one character
    /// matches most of an organisation and costs a round trip per keystroke.
    /// The browser filters its local "Go to" list instead.
    /// </summary>
    public const int MinLength = 2;

    /// <summary>
    /// Longer than this is not a search, it is a paste. Refused before any
    /// source is asked, so a long string never reaches a SQL <c>LIKE</c>.
    /// </summary>
    public const int MaxLength = 100;

    /// <summary>A query that no source is ever asked about.</summary>
    public static readonly PaletteQuery Unusable = new(string.Empty, string.Empty, [], []);

    private PaletteQuery(
        string raw, string folded, IReadOnlyList<string> terms, IReadOnlyList<string> sqlTerms)
    {
        Raw = raw;
        Folded = folded;
        Terms = terms;
        SqlTerms = sqlTerms;
    }

    /// <summary>The trimmed text as typed. Never logged above <c>Debug</c>.</summary>
    public string Raw { get; }

    /// <summary>
    /// The whole query, folded and with runs of whitespace collapsed to one
    /// space. This is what the prefix tier compares against — "is the whole
    /// query a prefix of the title or the short name".
    /// </summary>
    public string Folded { get; }

    /// <summary>
    /// The folded terms, in the order they were typed, deduplicated. Every one
    /// of them has to match somewhere for a candidate to survive ranking.
    /// </summary>
    public IReadOnlyList<string> Terms { get; }

    /// <summary>
    /// The same terms lower-cased but <b>not</b> accent-folded, for a source
    /// that pre-filters in SQL.
    ///
    /// <para>The expected shape is one <c>EF.Functions.ILike(column, "%" + term +
    /// "%")</c> per term, ANDed together, over the columns the source searches —
    /// which mirrors the every-term-must-match rule closely enough to narrow the
    /// read, with <see cref="PaletteRanking"/> making the real decision in
    /// memory afterwards.</para>
    ///
    /// <para><b>These terms are accent-sensitive, and that is a real
    /// limitation.</b> Postgres can only fold accents in SQL through the
    /// <c>unaccent</c> extension, which is not installed here — installing one is
    /// a migration and a conversation with the maintainer, not something a search
    /// box decides on its own. So <c>ILike</c> over <see cref="SqlTerms"/> finds
    /// "Møller" for <c>møller</c> but not for <c>moller</c>, while the in-memory
    /// ranking finds it either way. The rule that follows for a source author:
    /// <list type="bullet">
    ///   <item>A small table of human-typed names (solutions, environments — a
    ///   few hundred rows per organisation) should <b>not</b> pre-filter. Project
    ///   the searched columns for the rows the caller may see and let the ranking
    ///   decide; that is one indexed read and it folds accents correctly.</item>
    ///   <item>A large table (releases, recipes) should pre-filter with
    ///   <c>ILike</c> and over-return (ask for several times the limit it was
    ///   given, since ranking drops rows), accepting that an accent typed away
    ///   may miss.</item>
    /// </list></para>
    /// </summary>
    public IReadOnlyList<string> SqlTerms { get; }

    /// <summary>True when there is something to search for; false short-circuits the whole request.</summary>
    public bool IsUsable => Terms.Count > 0;

    /// <summary>
    /// Parses raw input into a query. Anything shorter than
    /// <see cref="MinLength"/> or longer than <see cref="MaxLength"/> after
    /// trimming comes back <see cref="Unusable"/>, so the caller can answer
    /// without touching the database.
    /// </summary>
    public static PaletteQuery Parse(string? raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length is < MinLength or > MaxLength) return Unusable;

        var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var terms = new List<string>(parts.Length);
        var sqlTerms = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var folded = Fold(part);
            if (folded.Length == 0 || terms.Contains(folded, StringComparer.Ordinal)) continue;
            terms.Add(folded);
            sqlTerms.Add(part.ToLowerInvariant());
        }

        return terms.Count == 0
            ? Unusable
            : new PaletteQuery(trimmed, Fold(trimmed), terms, sqlTerms);
    }

    /// <summary>
    /// Case- and accent-folds a string for comparison, and collapses runs of
    /// whitespace to a single space. Both sides of every comparison go through
    /// this, so a candidate's title is folded the same way the query is.
    ///
    /// <para>Folding is: lower-case (invariant), expand the Latin letters that
    /// have no canonical decomposition (<c>ø</c>, <c>æ</c>, <c>œ</c>, <c>ß</c>,
    /// <c>đ</c>, <c>ð</c>, <c>þ</c>, <c>ł</c>), then decompose and drop the
    /// combining marks — which covers everything else an accent can be written
    /// with (<c>é</c>, <c>ü</c>, <c>å</c>, <c>ñ</c>). The expansion list exists
    /// because those letters are single code points with no accent to strip:
    /// without it <c>moller</c> would not find "Møller", which is the worked
    /// example in <c>.design/command-palette.md</c>.</para>
    /// </summary>
    public static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var expanded = new StringBuilder(value.Length + 4);
        foreach (var ch in value.ToLowerInvariant())
        {
            switch (ch)
            {
                case 'ø': expanded.Append('o'); break;
                case 'æ': expanded.Append("ae"); break;
                case 'œ': expanded.Append("oe"); break;
                case 'ß': expanded.Append("ss"); break;
                case 'đ': expanded.Append('d'); break;
                case 'ð': expanded.Append('d'); break;
                case 'þ': expanded.Append("th"); break;
                case 'ł': expanded.Append('l'); break;
                default: expanded.Append(ch); break;
            }
        }

        string decomposed;
        try
        {
            decomposed = expanded.ToString().Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            // Unpaired surrogates reach us straight off a query string. Nothing
            // to decompose, but nothing to throw over either — fold what we have.
            decomposed = expanded.ToString();
        }

        var result = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }
            result.Append(ch);
        }

        return result.ToString();
    }
}
