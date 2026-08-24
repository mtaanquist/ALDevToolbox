using System.Globalization;
using System.Text;

namespace ALDevToolbox.Services.BcQuality;

/// <summary>
/// One knowledge article, parsed out of a BCQuality markdown file and ready to
/// upsert. Shaped by BCQuality's own schema contract (<c>skills/read.md</c> in
/// that repository); see <c>.design/bcquality.md</c> for what we keep and why.
/// </summary>
public sealed record BcQualityParsedArticle(
    string ArticleKey,
    string Layer,
    string Domain,
    string Slug,
    string Title,
    string Summary,
    string Content,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Technologies,
    IReadOnlyList<string> Countries,
    IReadOnlyList<string> ApplicationAreas,
    string BcVersionRaw,
    bool BcVersionAll,
    IReadOnlyList<int> BcVersions,
    int? BcVersionFrom,
    IReadOnlyList<BcQualityParsedSample> Samples);

/// <summary>A sample file that ships beside an article (<c>&lt;slug&gt;.&lt;kind&gt;.&lt;ext&gt;</c>).</summary>
public sealed record BcQualityParsedSample(string Kind, string FileName, string Language, string Content);

/// <summary>A file the walker refused, with the reason, so an operator can see what the mirror is missing.</summary>
public sealed record BcQualitySkippedFile(string Path, string Reason);

/// <summary>
/// Parsing for BCQuality's markdown: the small YAML-subset frontmatter block
/// and the sections the schema treats as load-bearing.
///
/// <para>
/// The frontmatter is hand-parsed rather than handed to a YAML library. The
/// schema is six fields of either a scalar or a single-line flow sequence
/// (<c>[a, b, c]</c>) — every one of the 256 published articles matches that
/// shape — so a real YAML parser would be a new dependency bought for nothing.
/// A file whose frontmatter does not fit is skipped rather than guessed at,
/// which is what BCQuality's contract requires of consumers: an invalid file
/// MUST be skipped, never partially parsed.
/// </para>
/// </summary>
public static class BcQualityMarkdown
{
    /// <summary>The six frontmatter fields BCQuality requires on every knowledge article.</summary>
    private static readonly string[] RequiredFields =
        ["bc-version", "domain", "keywords", "technologies", "countries", "application-area"];

    /// <summary>
    /// Parses one article. Returns null and sets <paramref name="reason"/> when
    /// the file violates the schema — the caller records it as a skip.
    /// </summary>
    /// <param name="articleKey">Repo-relative path, forward slashes (the citation key).</param>
    /// <param name="text">The raw file contents.</param>
    /// <param name="samples">Sibling sample files already read off disk.</param>
    public static BcQualityParsedArticle? TryParse(
        string articleKey,
        string text,
        IReadOnlyList<BcQualityParsedSample> samples,
        out string reason)
    {
        reason = string.Empty;

        var fields = TryReadFrontmatter(text, out var body);
        if (fields is null)
        {
            reason = "no YAML frontmatter block";
            return null;
        }

        var missing = RequiredFields.Where(f => !fields.ContainsKey(f) || fields[f].Length == 0).ToList();
        if (missing.Count > 0)
        {
            reason = "frontmatter is missing " + string.Join(", ", missing);
            return null;
        }

        var (summary, hasDescription) = ReadDescription(body);
        if (!hasDescription)
        {
            reason = "no '## Description' section";
            return null;
        }

        var bcVersionRaw = fields["bc-version"];
        if (!TryParseBcVersion(bcVersionRaw, out var all, out var versions, out var from))
        {
            reason = $"bc-version '{bcVersionRaw}' is not a recognised form";
            return null;
        }

        var segments = articleKey.Split('/');
        var layer = segments.Length > 0 ? segments[0] : string.Empty;
        var fileName = segments.Length > 0 ? segments[^1] : articleKey;
        var slug = fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^3]
            : fileName;

        return new BcQualityParsedArticle(
            articleKey,
            layer,
            ParseScalar(fields["domain"]),
            slug,
            ReadTitle(body) ?? slug,
            summary,
            body,
            ParseList(fields["keywords"]),
            ParseList(fields["technologies"]),
            ParseList(fields["countries"]),
            ParseList(fields["application-area"]),
            bcVersionRaw,
            all,
            versions,
            from,
            samples);
    }

    /// <summary>
    /// Splits the leading <c>---</c> block off the file. Returns null when the
    /// file does not open with one, or the block is never closed.
    /// </summary>
    private static Dictionary<string, string>? TryReadFrontmatter(string text, out string body)
    {
        body = text;
        var normalised = text.Replace("\r\n", "\n");
        if (!normalised.StartsWith("---\n", StringComparison.Ordinal)) return null;

        var end = normalised.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return null;

        var block = normalised[4..(end + 1)];
        // Skip past the closing "---" line itself.
        var afterMarker = normalised.IndexOf('\n', end + 1);
        body = afterMarker < 0 ? string.Empty : normalised[(afterMarker + 1)..].TrimStart('\n');

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in block.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            // A nested/multi-line YAML value (an indented key) is outside the
            // subset we accept; leaving it out makes the field look missing and
            // the file gets skipped, which is the contract's required outcome.
            if (rawLine.Length > 0 && char.IsWhiteSpace(rawLine[0])) continue;
            fields[key] = StripComment(line[(colon + 1)..].Trim());
        }
        return fields;
    }

    /// <summary>
    /// Drops a trailing <c>#</c> comment. Anything inside a flow sequence is
    /// left alone, so a <c>#</c> in a bracketed value survives.
    /// </summary>
    private static string StripComment(string value)
    {
        if (value.StartsWith('['))
        {
            var close = value.LastIndexOf(']');
            return close >= 0 ? value[..(close + 1)] : value;
        }
        var hash = value.IndexOf(" #", StringComparison.Ordinal);
        return hash >= 0 ? value[..hash].TrimEnd() : value;
    }

    /// <summary>Reads a scalar field, tolerating a single-entry flow sequence and surrounding quotes.</summary>
    private static string ParseScalar(string value)
    {
        var list = ParseList(value);
        return list.Count > 0 ? list[0] : string.Empty;
    }

    /// <summary>Reads <c>[a, b, c]</c> or a bare scalar into a list, lower-cased and de-duplicated.</summary>
    private static List<string> ParseList(string value)
    {
        var inner = value.StartsWith('[') && value.EndsWith(']')
            ? value[1..^1]
            : value;

        var result = new List<string>();
        foreach (var part in inner.Split(','))
        {
            var item = part.Trim().Trim('"', '\'').Trim();
            if (item.Length == 0) continue;
            var lowered = item.ToLowerInvariant();
            if (!result.Contains(lowered, StringComparer.Ordinal)) result.Add(lowered);
        }
        return result;
    }

    /// <summary>
    /// Expands the four accepted <c>bc-version</c> forms: the <c>[all]</c>
    /// sentinel, an explicit list, a closed range <c>[26..28]</c> (which the
    /// contract requires consumers to expand before comparing), and an
    /// open-ended <c>[26..]</c> (which is not enumerable and instead matches
    /// any target at or above the bound).
    /// </summary>
    internal static bool TryParseBcVersion(
        string raw, out bool all, out List<int> versions, out int? from)
    {
        all = false;
        versions = [];
        from = null;

        var inner = raw.Trim();
        if (inner.StartsWith('[') && inner.EndsWith(']')) inner = inner[1..^1];
        var tokens = inner.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        if (tokens.Count == 0) return false;

        foreach (var token in tokens)
        {
            if (string.Equals(token, "all", StringComparison.OrdinalIgnoreCase))
            {
                // The sentinel is mutually exclusive with explicit versions;
                // if a file combines them anyway, "applies everywhere" is the
                // safe reading and the explicit entries are harmless extras.
                all = true;
                continue;
            }

            var range = token.IndexOf("..", StringComparison.Ordinal);
            if (range < 0)
            {
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var single)) return false;
                if (!versions.Contains(single)) versions.Add(single);
                continue;
            }

            var lowText = token[..range].Trim();
            var highText = token[(range + 2)..].Trim();
            if (!int.TryParse(lowText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var low)) return false;

            if (highText.Length == 0)
            {
                // Open-ended. Keep the smallest bound seen, so two open ranges
                // in one file match the union rather than the intersection.
                from = from is { } existing ? Math.Min(existing, low) : low;
                continue;
            }

            if (!int.TryParse(highText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var high)) return false;
            if (high < low) return false;
            // A guard against an upstream typo turning into a million rows in
            // an int[]; real BC majors are two-digit.
            if (high - low > 200) return false;
            for (var v = low; v <= high; v++)
            {
                if (!versions.Contains(v)) versions.Add(v);
            }
        }

        versions.Sort();
        return all || versions.Count > 0 || from is not null;
    }

    /// <summary>The first level-1 heading, or null when the article has none.</summary>
    private static string? ReadTitle(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                return trimmed[2..].Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// Pulls the first paragraph of the required <c>## Description</c> section.
    /// That paragraph is what BCQuality calls "the primary retrieval target",
    /// so it is the one line worth showing an agent before it opens the
    /// article — and it is what the search snippet falls back to.
    /// Blockquote lines (the "contributions welcome" banner some community
    /// articles carry) are skipped.
    /// </summary>
    private static (string Summary, bool HasDescription) ReadDescription(string body)
    {
        var lines = body.Split('\n');
        var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("##", StringComparison.Ordinal)
                && trimmed.TrimStart('#').Trim().Equals("Description", StringComparison.OrdinalIgnoreCase))
            {
                start = i + 1;
                break;
            }
        }
        if (start < 0) return (string.Empty, false);

        var paragraph = new StringBuilder();
        for (var i = start; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("##", StringComparison.Ordinal)) break;
            if (trimmed.Length == 0)
            {
                if (paragraph.Length > 0) break;
                continue;
            }
            if (trimmed.StartsWith('>')) continue;
            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(trimmed);
        }
        return (paragraph.ToString(), true);
    }
}
