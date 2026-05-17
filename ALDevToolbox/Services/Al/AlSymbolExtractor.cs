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

    // Page-side field declaration: `field("Sell-to Customer No."; Rec."Sell-to Customer No.")`
    // No numeric id, expression after the semicolon. Captured so a page /
    // pageextension's outline carries the field rows and the
    // SourceFileOutlineGrouper can nest field-bound triggers
    // (OnValidate / OnLookup / OnAssistEdit / …) underneath them — the
    // ID-and-type table form misses the page case entirely.
    private static readonly Regex PageFieldDeclarationRegex = new(
        @"^\s*field\s*\(\s*(?<name>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s*;\s*(?<expr>[^)]+?)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Action declaration inside a page actions block: `action("Post")` or
    // `action(Post)`. Same treatment as page fields — actions own
    // OnAction triggers (and a handful of lifecycle triggers in newer AL),
    // so they need an outline anchor for nesting.
    private static readonly Regex ActionDeclarationRegex = new(
        @"^\s*action\s*\(\s*(?<name>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Query column / filter declaration: `column(Sum_Remaining_Amt_LCY;
    // "Remaining Amt. (LCY)")` and `filter(Document_Type; "Document Type")`
    // inside a `query`'s `dataitem` block. Surfaces as a member of the
    // query type so `MyQuery.Sum_Remaining_Amt_LCY` chains through the
    // resolver instead of stranding as a chain-step unresolved. The
    // source is always a single bare or quoted field name on the
    // surrounding dataitem's record — the simpler quoted-or-bare shape
    // for `expr` lets quoted identifiers carrying parens (like
    // <c>"Remaining Amt. (LCY)"</c>) round-trip correctly. Reports also
    // use `column(...)` with similar shape; we deliberately skip those
    // for now because nothing in the imported corpus accesses report
    // columns as chain members.
    private static readonly Regex QueryColumnDeclarationRegex = new(
        @"^\s*(?:column|filter)\s*\(\s*(?<name>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s*;\s*(?<expr>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s*\)",
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

    // Label-typed variable declaration:
    //   `UnsupportedTypeErr: Label 'Unsupported type %1.'[, Comment = ...][, Locked = ...];`
    // Matches the `<name>: Label` prefix; the content is extracted from
    // the raw line separately (StripCommentsAndStrings replaces string
    // contents with spaces, so we can't use the stripped text for the
    // payload).
    //
    // Why labels are worth pulling: when a customer reports an error
    // message without a stack trace, developers triangulate by searching
    // for the literal text. Surfacing labels as their own outline rows
    // makes that triangulation a left-click rather than a content search.
    private static readonly Regex LabelDeclarationRegex = new(
        @"^\s*(?<name>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s*:\s*Label\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Generic variable declaration: `Name: Type;` at start of line.
    // Captures the name token so ReleaseImportService can stamp source
    // line/column onto the matching ModuleVariable row (positions
    // unblock Go-to-definition on object-scope globals — see
    // .design/al-reference-extractor-refactor.md step 2). The trailing
    // `;` distinguishes a declaration from random `name: expr` shapes
    // that don't exist as standalone statements in AL grammar. Lines
    // that match LabelDeclarationRegex (the more specific Label form)
    // are filtered out by ordering — labels match first.
    //
    // Multi-name declarations (`A, B: Integer;`) emit nothing — the
    // name token doesn't permit commas. Rare for object-scope globals
    // in practice; if it ever shows up we'd extend the parser.
    private static readonly Regex VarDeclarationRegex = new(
        @"^\s*(?<name>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s*:\s*[^;]+;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
        var ownerKind = (string?)null; // populated from the object header so per-kind regexes can scope themselves

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
                    ownerKind = objMatch.Groups["type"].Value.ToLowerInvariant();
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
            // a harmless symbol row. Two shapes — table-side with id+type,
            // page-side with just name+expression — handled in priority
            // order so the more specific table form wins when both could
            // technically match.
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
                    // table-side field declaration. The regex shape
                    // (id; name; type) is unique to tables /
                    // tableextensions; pages use the name+expr form
                    // matched below. Disambiguated kinds let every
                    // downstream consumer read a context-free value
                    // (see .design/al-reference-extractor-refactor.md
                    // step 1).
                    Kind: "table_field",
                    Name: fieldName,
                    Signature: string.IsNullOrEmpty(fieldType) ? null : fieldType,
                    FieldId: fieldId,
                    LineNumber: i + 1,
                    ColumnStart: fColStart,
                    ColumnEnd: fColEnd));
                pendingEventKind = null;
                continue;
            }

            // Query column / filter declarations. Owner-kind gated so a
            // hypothetical `column(...)` outside a query body doesn't
            // accidentally consume the line. Emitted as `query_column`
            // so the chain walker resolves `MyQuery.ColumnName` against
            // these rows. (See .design/al-reference-extractor-refactor.md
            // step 1.)
            if (string.Equals(ownerKind, "query", StringComparison.OrdinalIgnoreCase))
            {
                var queryColMatch = QueryColumnDeclarationRegex.Match(stripped);
                if (queryColMatch.Success)
                {
                    var colRawName = queryColMatch.Groups["name"].Value;
                    var colName = Unquote(colRawName);
                    var colExpr = queryColMatch.Groups["expr"].Value.Trim();
                    var (qColStart, qColEnd) = FindNameColumns(rawLine, colRawName);
                    results.Add(new AlSymbol(
                        Kind: "query_column",
                        Name: colName,
                        Signature: string.IsNullOrEmpty(colExpr) ? null : colExpr,
                        FieldId: null,
                        LineNumber: i + 1,
                        ColumnStart: qColStart,
                        ColumnEnd: qColEnd));
                    pendingEventKind = null;
                    continue;
                }
            }

            var pageFieldMatch = PageFieldDeclarationRegex.Match(stripped);
            if (pageFieldMatch.Success)
            {
                var fieldRawName = pageFieldMatch.Groups["name"].Value;
                var fieldName = Unquote(fieldRawName);
                var expr = pageFieldMatch.Groups["expr"].Value.Trim();
                var (fColStart, fColEnd) = FindNameColumns(rawLine, fieldRawName);
                results.Add(new AlSymbol(
                    // page-side field declaration (name; expr). Page
                    // fields are local control names — not navigation
                    // targets — so the navigability filter in
                    // SourceFileViewer treats them differently from
                    // table_field. The shape disambiguates the owner
                    // kind without the consumer needing to join back
                    // to the object row.
                    Kind: "page_field",
                    Name: fieldName,
                    // Stash the source expression in Signature so the
                    // outline tooltip can show what the page field is
                    // bound to. Table-side fields use Signature for the
                    // AL type — same column, semantically parallel ("what
                    // does this field hold?").
                    Signature: string.IsNullOrEmpty(expr) ? null : expr,
                    FieldId: null,
                    LineNumber: i + 1,
                    ColumnStart: fColStart,
                    ColumnEnd: fColEnd));
                pendingEventKind = null;
                continue;
            }

            // Label declaration. Lives in object-level or procedure-local
            // var blocks. Signature carries the label content so the
            // outline / per-label tooltip can render it inline — same
            // column page-side fields use for "what's this bound to".
            var labelMatch = LabelDeclarationRegex.Match(stripped);
            if (labelMatch.Success)
            {
                var labelRawName = labelMatch.Groups["name"].Value;
                var labelName = Unquote(labelRawName);
                var (lColStart, lColEnd) = FindNameColumns(rawLine, labelRawName);
                // Pull the content from the raw line — the first
                // single-quoted string starting after the matched
                // `<name>: Label` prefix. Unterminated / multi-line
                // labels degrade gracefully to null content.
                var afterLabel = labelMatch.Index + labelMatch.Length;
                var content = ExtractFirstSingleQuotedString(rawLine, afterLabel);
                results.Add(new AlSymbol(
                    Kind: "label",
                    Name: labelName,
                    Signature: content,
                    FieldId: null,
                    LineNumber: i + 1,
                    ColumnStart: lColStart,
                    ColumnEnd: lColEnd));
                pendingEventKind = null;
                continue;
            }

            // Variable declarations inside object-scope or procedure-
            // local var blocks: `Name: Type;`. Used by
            // ReleaseImportService to stamp source line/column onto the
            // matching ModuleVariable row (procedure-local vars over-
            // emit here and are silently discarded by the name match).
            // Comes before the procedure/trigger declaration regex so a
            // line shaped like `Foo: Bar;` doesn't accidentally compete
            // with anything more specific upstream. Labels are picked
            // off above by the more specific LabelDeclarationRegex.
            var varMatch = VarDeclarationRegex.Match(stripped);
            if (varMatch.Success)
            {
                var varRawName = varMatch.Groups["name"].Value;
                var varName = Unquote(varRawName);
                var (vColStart, vColEnd) = FindNameColumns(rawLine, varRawName);
                results.Add(new AlSymbol(
                    Kind: "var_declaration",
                    Name: varName,
                    Signature: null,
                    FieldId: null,
                    LineNumber: i + 1,
                    ColumnStart: vColStart,
                    ColumnEnd: vColEnd));
                pendingEventKind = null;
                continue;
            }

            // Page action declaration. Gives field-style triggers
            // (OnAction in particular) an outline anchor to nest under,
            // mirroring how field-bound triggers nest under their field.
            var actionMatch = ActionDeclarationRegex.Match(stripped);
            if (actionMatch.Success)
            {
                var actionRawName = actionMatch.Groups["name"].Value;
                var actionName = Unquote(actionRawName);
                var (aColStart, aColEnd) = FindNameColumns(rawLine, actionRawName);
                results.Add(new AlSymbol(
                    // Page actions — page-local control names, not
                    // navigation targets. page_action carries the owner
                    // context directly so the navigability filter in
                    // SourceFileViewer doesn't need to join back to
                    // the object row.
                    Kind: "page_action",
                    Name: actionName,
                    Signature: null,
                    FieldId: null,
                    LineNumber: i + 1,
                    ColumnStart: aColStart,
                    ColumnEnd: aColEnd));
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
    /// Returns the content of the first single-quoted string in
    /// <paramref name="raw"/> starting at <paramref name="startIdx"/>.
    /// Doubled single quotes (<c>''</c>) un-escape to a literal
    /// apostrophe; an unterminated string returns null. Single-line —
    /// AL allows multi-line strings via concatenation, but label
    /// declarations in practice fit on one line.
    /// </summary>
    private static string? ExtractFirstSingleQuotedString(string raw, int startIdx)
    {
        var i = raw.IndexOf('\'', startIdx);
        if (i < 0) return null;
        i++;
        var sb = new System.Text.StringBuilder();
        while (i < raw.Length)
        {
            if (raw[i] == '\'')
            {
                if (i + 1 < raw.Length && raw[i + 1] == '\'')
                {
                    sb.Append('\'');
                    i += 2;
                    continue;
                }
                return sb.ToString();
            }
            sb.Append(raw[i]);
            i++;
        }
        return null;
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
