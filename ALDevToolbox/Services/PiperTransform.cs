using System.Globalization;
using System.Text;

namespace ALDevToolbox.Services;

/// <summary>
/// Output preset for <see cref="PiperTransform.Run"/>. The presets bake in
/// the per-item prefix / suffix / join so the common cases ("BC OR" and
/// "BC AND" filter strings, SQL <c>IN</c>-style lists) don't need a trip
/// through <see cref="PiperOutputFormat.Custom"/>.
/// </summary>
public enum PiperOutputFormat
{
    /// <summary>BC filter OR: items joined with <c>|</c>.</summary>
    BcOr,
    /// <summary>BC filter AND with exclusion: each item prefixed with <c>&lt;&gt;</c>, joined with <c>&amp;</c>.</summary>
    BcAnd,
    /// <summary>SQL <c>IN</c>-clause body: each item quoted with <c>'</c>, joined with <c>,</c>. Embedded <c>'</c> is doubled.</summary>
    Sql,
    /// <summary>User-supplied per-item prefix, per-item suffix, and join string.</summary>
    Custom,
}

public enum PiperSortOrder { None, Ascending, Descending }

/// <summary>
/// Options bag for <see cref="PiperTransform.Run"/>. All fields have safe
/// defaults so callers can construct <c>new PiperOptions()</c> and only set
/// what they need.
/// </summary>
public sealed record PiperOptions
{
    public PiperOutputFormat Format { get; init; } = PiperOutputFormat.BcOr;
    public string CustomPrefix { get; init; } = "";
    public string CustomSuffix { get; init; } = "";
    public string CustomJoin { get; init; } = ",";
    public string ResultPrefix { get; init; } = "";
    public string ResultSuffix { get; init; } = "";

    /// <summary>
    /// When non-empty, splits on this exact string (with <c>\t</c>, <c>\n</c>,
    /// <c>\r</c>, and <c>\\</c> recognised as escapes). When empty, the
    /// transform auto-detects the most prolific delimiter from a fixed set.
    /// </summary>
    public string SeparatorOverride { get; init; } = "";

    public PiperSortOrder Sort { get; init; } = PiperSortOrder.None;

    /// <summary>Restricts auto-detect to <c>\r\n</c>, <c>\n</c>, <c>\r</c>.</summary>
    public bool SplitOnNewlinesOnly { get; init; }

    public bool TrimItems { get; init; }

    /// <summary>
    /// Drops items that are empty after trimming. Defaults to <c>true</c>
    /// because <c>a,,b</c> is almost always a typo, not three intentional
    /// items. Set <c>false</c> to keep the original behaviour.
    /// </summary>
    public bool SkipEmpty { get; init; } = true;

    /// <summary>
    /// Removes duplicate items, preserving first-occurrence order.
    /// Defaults to <c>true</c> because the most common use of Piper is
    /// building a BC OR or SQL <c>IN</c> list where duplicates are noise.
    /// </summary>
    public bool RemoveDuplicates { get; init; } = true;
}

/// <summary>
/// Result of one transform pass. <see cref="DetectedSeparatorDisplay"/> is
/// non-null only when auto-detect actually found a delimiter — callers use
/// it to hint the user what's being used (e.g. as a placeholder).
/// </summary>
public sealed record PiperResult(
    string Output,
    int ItemCount,
    string DelimiterDescription,
    string? DetectedSeparatorDisplay);

/// <summary>
/// Pure string transform that powers the <c>/piper</c> page. Lives in
/// <c>Services/</c> rather than under a <c>PiperService</c> DI registration
/// because there is no DB access, no async, and no per-request state — it's
/// a static helper, called per-keystroke from the page.
/// </summary>
public static class PiperTransform
{
    // Auto-detect candidate order matters: \r\n leads so Windows line-endings
    // aren't double-counted as separate \r and \n. "|" is in the set so a BC
    // OR result round-trips when the user feeds it back via Swap.
    private static readonly string[] FullCandidates = { "\r\n", "\n", "\r", "\t", ",", ";", "|" };
    private static readonly string[] NewlineOnlyCandidates = { "\r\n", "\n", "\r" };

    public static PiperResult Run(string input, PiperOptions options)
    {
        if (string.IsNullOrEmpty(input))
        {
            return new PiperResult("", 0, "Awaiting input…", null);
        }

        var (items, delimiterDesc, detectedDisplay) = Split(input, options);

        if (options.TrimItems)
        {
            for (var i = 0; i < items.Length; i++) items[i] = items[i].Trim();
        }

        if (options.SkipEmpty)
        {
            items = items.Where(i => i.Length > 0).ToArray();
        }

        if (options.RemoveDuplicates)
        {
            items = items.Distinct().ToArray();
        }

        if (options.Sort != PiperSortOrder.None && items.Length > 1)
        {
            items = SortItems(items, options.Sort);
        }

        var formatted = options.Format switch
        {
            PiperOutputFormat.BcAnd => string.Join("&", items.Select(i => "<>" + i)),
            PiperOutputFormat.Sql => string.Join(",", items.Select(i => $"'{i.Replace("'", "''")}'")),
            PiperOutputFormat.Custom => string.Join(options.CustomJoin, items.Select(i => options.CustomPrefix + i + options.CustomSuffix)),
            _ => string.Join("|", items),
        };

        if (options.ResultPrefix.Length > 0 || options.ResultSuffix.Length > 0)
        {
            formatted = options.ResultPrefix + formatted + options.ResultSuffix;
        }

        return new PiperResult(formatted, items.Length, delimiterDesc, detectedDisplay);
    }

    private static (string[] Items, string DelimiterDescription, string? DetectedDisplay) Split(string input, PiperOptions options)
    {
        var overrideLiteral = UnescapeSeparator(options.SeparatorOverride);

        if (!string.IsNullOrEmpty(overrideLiteral))
        {
            var items = input.Split(new[] { overrideLiteral }, StringSplitOptions.None);
            return (items, $"custom: {FriendlyDelim(overrideLiteral)}", null);
        }

        var candidates = options.SplitOnNewlinesOnly ? NewlineOnlyCandidates : FullCandidates;
        string bestDelim = "";
        int bestCount = 0;
        foreach (var c in candidates)
        {
            var count = CountOccurrences(input, c);
            if (count > bestCount)
            {
                bestCount = count;
                bestDelim = c;
            }
        }

        if (bestCount == 0)
        {
            return (new[] { input }, "no delimiter found", null);
        }

        var split = input.Split(new[] { bestDelim }, StringSplitOptions.None);
        return (split, $"auto-detected: {FriendlyDelim(bestDelim)}", EscapeForDisplay(bestDelim));
    }

    // Sorts numerically when every item parses as a decimal (typical for AL
    // ID lists where lexical order would put "999" after "1001"); otherwise
    // case-insensitive ordinal.
    private static string[] SortItems(string[] items, PiperSortOrder order)
    {
        var allNumeric = items.All(i => decimal.TryParse(
            i, NumberStyles.Number, CultureInfo.InvariantCulture, out _));

        IEnumerable<string> sorted = allNumeric
            ? (order == PiperSortOrder.Ascending
                ? items.OrderBy(i => decimal.Parse(i, CultureInfo.InvariantCulture))
                : items.OrderByDescending(i => decimal.Parse(i, CultureInfo.InvariantCulture)))
            : (order == PiperSortOrder.Ascending
                ? items.OrderBy(i => i, StringComparer.OrdinalIgnoreCase)
                : items.OrderByDescending(i => i, StringComparer.OrdinalIgnoreCase));

        return sorted.ToArray();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) != -1)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    // Lets the user type literal "\t" / "\n" / "\r" / "\\" in the separator
    // override input instead of needing to paste actual whitespace.
    internal static string UnescapeSeparator(string s)
    {
        if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                switch (s[i + 1])
                {
                    case 't': sb.Append('\t'); i++; continue;
                    case 'n': sb.Append('\n'); i++; continue;
                    case 'r': sb.Append('\r'); i++; continue;
                    case '\\': sb.Append('\\'); i++; continue;
                }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static string FriendlyDelim(string s) => $"\"{EscapeForDisplay(s)}\"";

    internal static string EscapeForDisplay(string s) => s switch
    {
        "\t" => "\\t",
        "\n" => "\\n",
        "\r" => "\\r",
        "\r\n" => "\\r\\n",
        _ => s,
    };
}
