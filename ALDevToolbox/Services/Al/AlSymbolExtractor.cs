using System.Text.RegularExpressions;

namespace ALDevToolbox.Services.Al;

/// <summary>
/// Extracts procedure / trigger / event publisher / event subscriber
/// declarations from AL source for the Object Explorer symbol index.
/// Walks the file line-by-line; one declaration per matched line. Overloads
/// produce one row each (the references query later merges them).
///
/// Not a full AL grammar — recognises the declaration shape and the two
/// attributes that distinguish event publishers / subscribers from regular
/// procedures. Comments and string literals are stripped before matching
/// so a <c>// procedure NotReally(...)</c> line is ignored.
/// </summary>
public static class AlSymbolExtractor
{
    private static readonly Regex DeclarationRegex = new(
        @"^\s*(?<scope>local\s+|internal\s+|protected\s+)?" +
        @"(?<kind>procedure|trigger)\s+" +
        @"(?<name>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)" +
        @"\s*(?<sig>\([^)]*\))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IntegrationEventAttrRegex = new(
        @"^\s*\[\s*(IntegrationEvent|BusinessEvent)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EventSubscriberAttrRegex = new(
        @"^\s*\[\s*EventSubscriber\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AttrLineRegex = new(
        @"^\s*\[",
        RegexOptions.Compiled);

    /// <summary>
    /// Returns the declarations found in <paramref name="source"/>. The line
    /// number is 1-based and refers to the original (un-stripped) source so
    /// the click-target lines up with what CodeMirror renders.
    /// </summary>
    public static IReadOnlyList<AlSymbol> Extract(string source)
    {
        if (string.IsNullOrEmpty(source)) return Array.Empty<AlSymbol>();

        // Trim a leading UTF-8 BOM, if any survived the stream reader.
        if (source[0] == '﻿') source = source.Substring(1);

        var results = new List<AlSymbol>();
        var lines = source.Replace("\r\n", "\n").Split('\n');

        var inBlockComment = false;
        var pendingEventKind = (string?)null; // "publisher" / "subscriber" / null

        for (var i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            var stripped = StripCommentsAndStrings(rawLine, ref inBlockComment);

            if (string.IsNullOrWhiteSpace(stripped))
            {
                // Pure-whitespace / pure-comment line — keep the pending
                // attribute marker so the decl can still bind to it.
                continue;
            }

            // Attribute line: classify it and skip without resetting the
            // pending marker; AL allows multiple attributes stacked above a
            // declaration (e.g. [EventSubscriber(...)] [HandlerFunctions(...)]).
            if (AttrLineRegex.IsMatch(stripped))
            {
                if (IntegrationEventAttrRegex.IsMatch(stripped))
                {
                    pendingEventKind = "publisher";
                }
                else if (EventSubscriberAttrRegex.IsMatch(stripped))
                {
                    pendingEventKind = "subscriber";
                }
                continue;
            }

            var match = DeclarationRegex.Match(stripped);
            if (!match.Success)
            {
                // Non-declaration code line — clear the pending event marker.
                pendingEventKind = null;
                continue;
            }

            var rawKind = match.Groups["kind"].Value.ToLowerInvariant();
            var scope = match.Groups["scope"].Success
                ? match.Groups["scope"].Value.Trim().ToLowerInvariant()
                : string.Empty;
            var rawName = match.Groups["name"].Value;
            var name = rawName.StartsWith('"') && rawName.EndsWith('"') && rawName.Length >= 2
                ? rawName.Substring(1, rawName.Length - 2)
                : rawName;
            var signature = match.Groups["sig"].Success ? match.Groups["sig"].Value : null;

            var kind = ClassifyKind(rawKind, scope, pendingEventKind);

            // Column tracking needs to use the *raw* line because stripping
            // may shorten it (block-comment open/close mid-line). Find the
            // name token in the raw text by searching after the kind keyword.
            var (columnStart, columnEnd) = FindNameColumns(rawLine, rawName);

            results.Add(new AlSymbol(
                Kind: kind,
                Name: name,
                Signature: signature,
                LineNumber: i + 1,
                ColumnStart: columnStart,
                ColumnEnd: columnEnd));

            pendingEventKind = null;
        }

        return results;
    }

    private static string ClassifyKind(string rawKind, string scope, string? pendingEventKind)
    {
        if (rawKind == "trigger") return "trigger";

        // pendingEventKind only meaningful for procedures.
        if (pendingEventKind == "publisher") return "event_publisher";
        if (pendingEventKind == "subscriber") return "event_subscriber";

        return scope switch
        {
            "local" => "local_procedure",
            "internal" => "internal_procedure",
            "protected" => "protected_procedure",
            _ => "procedure",
        };
    }

    /// <summary>
    /// Walks <paramref name="rawLine"/> looking for the quoted-or-unquoted
    /// <paramref name="rawName"/> token (the raw text the regex captured).
    /// Returns 1-based column start/end. Used by CodeMirror to draw the
    /// click affordance over exactly the name token, ignoring leading
    /// whitespace, scope keyword, and "procedure"/"trigger" lexeme.
    /// </summary>
    private static (int Start, int End) FindNameColumns(string rawLine, string rawName)
    {
        var idx = rawLine.IndexOf(rawName, StringComparison.Ordinal);
        if (idx < 0)
        {
            // Comment/string stripping shifted the layout enough that the
            // raw name isn't at the same index. Fall back to a non-pointy
            // value rather than throwing — the symbol still works as a
            // references target, only the click-affordance positioning is
            // approximate.
            return (1, 1 + rawName.Length);
        }
        return (idx + 1, idx + 1 + rawName.Length);
    }

    /// <summary>
    /// Replaces comments and string contents on a single line with spaces
    /// so the regex matcher only sees code. Block comments are stateful
    /// across lines (passed via <paramref name="inBlockComment"/>); strings
    /// are line-local.
    /// </summary>
    private static string StripCommentsAndStrings(string line, ref bool inBlockComment)
    {
        var sb = new System.Text.StringBuilder(line.Length);
        var i = 0;
        var n = line.Length;
        while (i < n)
        {
            if (inBlockComment)
            {
                if (i + 1 < n && line[i] == '*' && line[i + 1] == '/')
                {
                    inBlockComment = false;
                    sb.Append("  ");
                    i += 2;
                    continue;
                }
                sb.Append(' ');
                i++;
                continue;
            }

            // Line comment — replace the rest of the line with spaces.
            if (i + 1 < n && line[i] == '/' && line[i + 1] == '/')
            {
                sb.Append(' ', n - i);
                break;
            }

            // Block comment open.
            if (i + 1 < n && line[i] == '/' && line[i + 1] == '*')
            {
                inBlockComment = true;
                sb.Append("  ");
                i += 2;
                continue;
            }

            // Single-quoted string — replace contents (and the closing quote)
            // with spaces to keep column offsets stable.
            if (line[i] == '\'')
            {
                sb.Append(' ');
                i++;
                while (i < n)
                {
                    if (line[i] == '\'' && i + 1 < n && line[i + 1] == '\'')
                    {
                        sb.Append("  ");
                        i += 2;
                        continue;
                    }
                    if (line[i] == '\'')
                    {
                        sb.Append(' ');
                        i++;
                        break;
                    }
                    sb.Append(' ');
                    i++;
                }
                continue;
            }

            sb.Append(line[i]);
            i++;
        }
        return sb.ToString();
    }
}

/// <summary>
/// One declaration found by <see cref="AlSymbolExtractor.Extract"/>.
/// Line/column are 1-based against the original source.
/// </summary>
public sealed record AlSymbol(
    string Kind,
    string Name,
    string? Signature,
    int LineNumber,
    int ColumnStart,
    int ColumnEnd);
