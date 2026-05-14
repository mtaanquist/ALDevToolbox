namespace ALDevToolbox.Services.Al;

/// <summary>
/// Scans an AL file for identifier tokens whose name matches the resolvable
/// vocabulary, and emits the ranges that the file viewer should underline.
/// Two classes of resolvable token are recognised:
///
/// - <b>Symbol references</b> (procedures, event publishers, event subscribers).
///   These resolve only at <em>call sites</em> — the token must be followed
///   by <c>(</c>. Without the paren we'd underline every identifier that
///   happens to share a procedure name (variable names, namespace segments,
///   property keys, etc.).
/// - <b>Object references</b> (table, codeunit, page, etc. names). These only
///   resolve when preceded by an object keyword (<c>Record "Sales Header"</c>,
///   <c>Codeunit::"Sales-Post"</c>) — a bare <c>Item</c> appearing as a
///   variable name or text would otherwise drag a misleading underline.
///
/// Lines starting with <c>using</c> or <c>namespace</c> are skipped entirely:
/// the segments after the dot are just naming-tree breadcrumbs, not symbol
/// references in the AL sense, and matching them on a name collision is
/// always wrong.
///
/// Not a full parser. Comments and string literals are stripped (replaced
/// with spaces so column offsets stay stable) before tokenising. Comparisons
/// are case-insensitive because AL is case-insensitive.
/// </summary>
public static class AlResolvableTokenScanner
{
    // Keywords whose immediately-following token is a user-defined object
    // name the symbol index can resolve to a file. Drawn from the AL data
    // type catalogue — only the keywords that actually take a named object
    // are listed. Primitive types (Text, Integer, …) are deliberately
    // excluded so `var Msg: Text "Lbl"` doesn't mark "Lbl" as jumpable.
    // RecordRef / FieldRef / KeyRef don't take a name either.
    private static readonly HashSet<string> ObjectKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Top-level object types that can be referenced by name from a
        // variable declaration, property, or `Codeunit::"X"` typed expression.
        "codeunit", "table", "page", "report", "query", "xmlport",
        "enum", "interface", "permissionset", "profile", "controladdin",
        "record",
        // Test-only references — `TestPage "Customer Card"` ⇒ resolve to page.
        "requestpage", "testpage", "testpart", "testrequestpage",
        // Extension declarations: the name after `extends` is the base object.
        "pageextension", "tableextension", "reportextension", "enumextension",
        "permissionsetextension",
        "extends",
    };

    /// <summary>
    /// Walks <paramref name="source"/> and emits one range per identifier whose
    /// unquoted name appears in either vocabulary set. Ranges are 1-based and
    /// emitted in document order (ascending line then column).
    /// </summary>
    public static IReadOnlyList<ResolvableTokenRange> Scan(
        string source, ResolvableVocabulary vocabulary)
    {
        if (string.IsNullOrEmpty(source)) return Array.Empty<ResolvableTokenRange>();
        if (vocabulary.ObjectNames.Count == 0 && vocabulary.SymbolNames.Count == 0)
        {
            return Array.Empty<ResolvableTokenRange>();
        }

        if (source[0] == '﻿') source = source.Substring(1);

        var ranges = new List<ResolvableTokenRange>();
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var inBlockComment = false;

        for (var li = 0; li < lines.Length; li++)
        {
            var stripped = StripCommentsAndStrings(lines[li], ref inBlockComment);
            if (IsNamespaceLine(stripped)) continue;
            ScanLine(stripped, li + 1, vocabulary, ranges);
        }

        return ranges;
    }

    /// <summary>
    /// Lines that begin with <c>using</c> or <c>namespace</c> are AL's
    /// naming-tree imports / declarations. The dotted segments aren't symbol
    /// references — matching them on a name collision underlines a thing the
    /// user can't jump to.
    /// </summary>
    private static bool IsNamespaceLine(string stripped)
    {
        var i = 0;
        while (i < stripped.Length && char.IsWhiteSpace(stripped[i])) i++;
        return MatchesKeyword(stripped, i, "using")
            || MatchesKeyword(stripped, i, "namespace");
    }

    private static bool MatchesKeyword(string s, int offset, string keyword)
    {
        if (offset + keyword.Length > s.Length) return false;
        for (var k = 0; k < keyword.Length; k++)
        {
            var c = s[offset + k];
            if (char.ToLowerInvariant(c) != keyword[k]) return false;
        }
        var after = offset + keyword.Length;
        return after == s.Length || !IsIdentifierChar(s[after]);
    }

    private static void ScanLine(
        string lineText, int oneBasedLineNumber,
        ResolvableVocabulary vocab, List<ResolvableTokenRange> ranges)
    {
        var i = 0;
        var n = lineText.Length;
        while (i < n)
        {
            var c = lineText[i];

            if (c == '"')
            {
                var end = lineText.IndexOf('"', i + 1);
                if (end < 0) break;
                var name = lineText.Substring(i + 1, end - i - 1);
                if (name.Length > 0 && IsResolvable(lineText, i, end + 1, name, vocab))
                {
                    ranges.Add(new ResolvableTokenRange(
                        oneBasedLineNumber, i + 1, end + 2));
                }
                i = end + 1;
                continue;
            }

            if (IsIdentifierStart(c))
            {
                var start = i;
                while (i < n && IsIdentifierChar(lineText[i])) i++;
                var name = lineText.Substring(start, i - start);
                if (IsResolvable(lineText, start, i, name, vocab))
                {
                    ranges.Add(new ResolvableTokenRange(
                        oneBasedLineNumber, start + 1, i + 1));
                }
                continue;
            }

            i++;
        }
    }

    private static bool IsResolvable(
        string lineText, int tokenStart, int tokenEnd, string name, ResolvableVocabulary vocab)
    {
        // Symbol references (procedures, events) only resolve at call sites
        // — the token must be followed by `(`. Without that check we'd
        // underline every identifier that happens to share a procedure
        // name (variable names, namespace segments, property keys, etc.).
        if (vocab.SymbolNames.Contains(name)
            && IsFollowedByOpenParen(lineText, tokenEnd))
        {
            return true;
        }

        // Object references only resolve in a keyword-preceded context:
        // `Record "Sales Header"`, `Codeunit::"Sales-Post"`. Without the
        // context, the token is most likely a variable name or unrelated
        // text, and underlining it sets the wrong expectation. Checked
        // even when the name also appears in SymbolNames — a single name
        // can legitimately be both an object and a procedure, with the
        // shape of the surrounding code disambiguating.
        if (vocab.ObjectNames.Contains(name)
            && HasObjectKeywordContext(lineText, tokenStart))
        {
            return true;
        }

        return false;
    }

    private static bool IsFollowedByOpenParen(string lineText, int tokenEnd)
    {
        var i = tokenEnd;
        while (i < lineText.Length && char.IsWhiteSpace(lineText[i])) i++;
        return i < lineText.Length && lineText[i] == '(';
    }

    private static bool HasObjectKeywordContext(string lineText, int tokenStart)
    {
        var i = tokenStart - 1;
        while (i >= 0 && char.IsWhiteSpace(lineText[i])) i--;
        if (i < 0) return false;

        // `::` operator — typed reference, e.g. `Codeunit::"Sales-Post"`.
        if (i >= 1 && lineText[i] == ':' && lineText[i - 1] == ':') return true;

        // Trailing identifier — is it an object keyword like `Codeunit`?
        if (!IsIdentifierChar(lineText[i])) return false;
        var kend = i;
        while (kend >= 0 && IsIdentifierChar(lineText[kend])) kend--;
        var keyword = lineText.Substring(kend + 1, i - kend);
        return ObjectKeywords.Contains(keyword);
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

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

            if (i + 1 < n && line[i] == '/' && line[i + 1] == '/')
            {
                sb.Append(' ', n - i);
                break;
            }

            if (i + 1 < n && line[i] == '/' && line[i + 1] == '*')
            {
                inBlockComment = true;
                sb.Append("  ");
                i += 2;
                continue;
            }

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
/// Vocabulary buckets fed to <see cref="AlResolvableTokenScanner.Scan"/>.
/// <see cref="ObjectNames"/> are file-level identifiers (Sales Header,
/// Sales-Post) — only resolvable in a keyword-preceded context.
/// <see cref="SymbolNames"/> are callable identifiers (procedures, events)
/// — resolvable standalone.
/// </summary>
public sealed record ResolvableVocabulary(
    IReadOnlySet<string> ObjectNames,
    IReadOnlySet<string> SymbolNames);

/// <summary>
/// One range that the file viewer should underline as resolvable. Columns are
/// 1-based; <see cref="ColumnEnd"/> is exclusive (matches the convention used
/// by <c>BaseAppSymbol</c>).
/// </summary>
public sealed record ResolvableTokenRange(int Line, int ColumnStart, int ColumnEnd);
