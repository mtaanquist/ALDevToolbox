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

    // Field declaration inside a table / tableextension fields block.
    // `field(1; "No."; Code[20])` — the type runs to the closing paren but
    // may itself contain brackets (e.g. `Code[20]`). The regex matches up
    // to the *first* `)` that closes the field declaration; trailing
    // content like `{ ... }` or a same-line `{ }` is left alone.
    private static readonly Regex FieldDeclarationRegex = new(
        @"^\s*field\s*\(\s*(?<id>\d+)\s*;\s*(?<name>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s*;\s*(?<type>[^)]+?)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The object-header declaration line: `table 36 "Sales Header"`,
    // `codeunit 80 "Sales-Post"`, `tableextension 7300 "ItemExt" extends "Item"`
    // etc. We only care about the *primary* object name (not the `extends`
    // target — that's already covered by the reference scanner). Mirrors the
    // shape used by `AlDeclarationParser.DeclarationRegex`.
    private static readonly Regex ObjectDeclarationRegex = new(
        @"^\s*(?<type>codeunit|tableextension|pageextension|reportextension|enumextension|permissionsetextension|pagecustomization|table|page|report|query|xmlport|controladdin|enum|interface|permissionset|profile|dotnet)\b" +
        @"(?:\s+(?<id>\d+))?" +
        @"\s+(?<name>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)",
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
        var objectDeclEmitted = false;

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

            // Object header (`table 36 "Sales Header"`). Only the first match
            // counts; later occurrences of the same shape inside the body —
            // unlikely but defensible — are ignored. Emitted before the
            // attribute branch because the header line never carries an
            // attribute.
            if (!objectDeclEmitted)
            {
                var objMatch = ObjectDeclarationRegex.Match(stripped);
                if (objMatch.Success)
                {
                    var objRawName = objMatch.Groups["name"].Value;
                    var objName = Unquote(objRawName);
                    var (objColStart, objColEnd) = FindNameColumns(rawLine, objRawName);
                    results.Add(new AlSymbol(
                        Kind: "object_declaration",
                        Name: objName,
                        Signature: null,
                        FieldId: null,
                        LineNumber: i + 1,
                        ColumnStart: objColStart,
                        ColumnEnd: objColEnd));
                    objectDeclEmitted = true;
                    continue;
                }
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

            // Field declarations. Independent of the procedure regex — they
            // only appear inside a `fields { ... }` block in practice, and
            // any false match outside that block (vanishingly rare) is still
            // a harmless symbol row.
            var fieldMatch = FieldDeclarationRegex.Match(stripped);
            if (fieldMatch.Success)
            {
                var fieldRawName = fieldMatch.Groups["name"].Value;
                var fieldName = Unquote(fieldRawName);
                var fieldType = fieldMatch.Groups["type"].Value.Trim();
                int? fieldId = null;
                if (int.TryParse(fieldMatch.Groups["id"].Value, out var parsedId))
                {
                    fieldId = parsedId;
                }
                var (fColStart, fColEnd) = FindNameColumns(rawLine, fieldRawName);
                results.Add(new AlSymbol(
                    Kind: "field",
                    Name: fieldName,
                    Signature: string.IsNullOrEmpty(fieldType) ? null : fieldType,
                    FieldId: fieldId,
                    LineNumber: i + 1,
                    ColumnStart: fColStart,
                    ColumnEnd: fColEnd));
                pendingEventKind = null;
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
            var name = Unquote(rawName);
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
                FieldId: null,
                LineNumber: i + 1,
                ColumnStart: columnStart,
                ColumnEnd: columnEnd));

            pendingEventKind = null;
        }

        return results;
    }

    private static string Unquote(string raw)
        => raw.StartsWith('"') && raw.EndsWith('"') && raw.Length >= 2
            ? raw.Substring(1, raw.Length - 2)
            : raw;

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
/// Line/column are 1-based against the original source. <see cref="FieldId"/>
/// is populated for <c>field</c> rows (the AL field number) and <c>null</c>
/// for every other kind.
/// </summary>
public sealed record AlSymbol(
    string Kind,
    string Name,
    string? Signature,
    int? FieldId,
    int LineNumber,
    int ColumnStart,
    int ColumnEnd);
