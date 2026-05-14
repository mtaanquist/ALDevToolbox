using System.Text.RegularExpressions;

namespace ALDevToolbox.Services.Al;

/// <summary>
/// Resolves a "Go to definition" click in the Object Explorer. Given a click
/// position inside a file, returns the token under the cursor plus minimal
/// context (was it qualified by a variable, was it inside an object-reference
/// keyword block, etc.) so the caller can look up the right declaration.
///
/// Not a full AL parser — recognises the common shapes that account for
/// almost all real-world definitions:
///
///   - <c>Codeunit "Sales-Post"</c>           → object reference, token is the name.
///   - <c>Codeunit::"Sales-Post"</c>          → object reference with double-colon.
///   - <c>"Sales-Post".Post(...)</c>          → object reference followed by member.
///   - <c>SalesPostCu.Post(...)</c>           → variable-qualified procedure call.
///   - <c>Post(...)</c>                        → bare procedure call.
/// </summary>
public static class AlGoToDefinitionLocator
{
    private static readonly HashSet<string> ObjectKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "codeunit", "table", "page", "report", "query", "xmlport", "controladdin",
        "enum", "interface", "permissionset", "profile",
        "pageextension", "tableextension", "reportextension", "enumextension",
        "permissionsetextension",
    };

    /// <summary>
    /// Inspects <paramref name="source"/> at the supplied 1-based line and
    /// column and returns the click context. Returns <c>null</c> when the
    /// cursor lands on whitespace or a non-identifier character.
    /// </summary>
    public static GoToDefinitionClick? Inspect(string source, int line, int column)
    {
        if (string.IsNullOrEmpty(source) || line < 1 || column < 1) return null;

        var lineText = GetLine(source, line);
        if (lineText is null) return null;

        // CodeMirror passes a 1-based column for the click position; convert
        // to a 0-based index into the line.
        var idx = Math.Min(Math.Max(0, column - 1), lineText.Length - 1);
        if (idx < 0 || idx >= lineText.Length) return null;

        var (word, wordStart, wordEnd) = ExtractTokenAt(lineText, idx);
        if (string.IsNullOrEmpty(word)) return null;

        // Left context: what's immediately before the token (skipping
        // whitespace)? Captures `.`, `::`, or an identifier keyword like
        // <c>Codeunit</c>.
        var leftContext = ReadLeftContext(lineText, wordStart);

        return new GoToDefinitionClick(
            Word: word,
            LineText: lineText,
            LeftContext: leftContext);
    }

    /// <summary>
    /// Returns the type name when <paramref name="fileContent"/> contains a
    /// var declaration like <c>VarName: Codeunit "Sales-Post"</c>. Used to
    /// resolve <c>SalesPostCu.Post(...)</c> to the right declaring object.
    /// Searches the whole file rather than tracking scope — AL devs rarely
    /// reuse a variable name across procedures, and even when they do, both
    /// vars usually share a type.
    /// </summary>
    public static string? ResolveVariableType(string fileContent, string variableName)
    {
        if (string.IsNullOrEmpty(fileContent) || string.IsNullOrEmpty(variableName)) return null;
        // Match `VarName: Codeunit "Some Name"` or `VarName: Codeunit Identifier`.
        // Record is by far the most common form for field-access targets and
        // was missing here — `MfgSetup."Dynamic Low-Level Code"` couldn't
        // resolve because the qualifier walked off `Record "Manufacturing Setup"`.
        var escaped = Regex.Escape(variableName);
        var pattern = $@"\b{escaped}\s*:\s*(?:Record|Codeunit|Page|Report|Query|XmlPort|Interface|Enum)\s+(""(?<q>[^""]+)""|(?<u>[A-Za-z_][A-Za-z0-9_]*))";
        var match = Regex.Match(fileContent, pattern, RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        return match.Groups["q"].Success
            ? match.Groups["q"].Value
            : match.Groups["u"].Value;
    }

    private static string? GetLine(string source, int line)
    {
        var i = 0;
        var n = source.Length;
        var current = 1;
        var lineStart = 0;
        while (i < n)
        {
            if (source[i] == '\n')
            {
                if (current == line)
                {
                    var lineEnd = i;
                    if (lineEnd > 0 && source[lineEnd - 1] == '\r') lineEnd--;
                    return source.Substring(lineStart, lineEnd - lineStart);
                }
                current++;
                lineStart = i + 1;
            }
            i++;
        }
        if (current == line)
        {
            return source.Substring(lineStart);
        }
        return null;
    }

    /// <summary>
    /// Extracts the token straddling <paramref name="idx"/> on
    /// <paramref name="line"/>. Handles double-quoted identifiers
    /// (AL's <c>"Sales-Post"</c> form, spaces and hyphens allowed inside)
    /// and bare identifiers. Returns empty string when the index lands on
    /// punctuation or whitespace.
    /// </summary>
    private static (string Word, int Start, int End) ExtractTokenAt(string line, int idx)
    {
        // Quoted identifier: scan for a containing pair of quotes on this line.
        var quoteIdx = FindContainingQuotes(line, idx);
        if (quoteIdx is { } pair)
        {
            return (line.Substring(pair.openIdx + 1, pair.closeIdx - pair.openIdx - 1),
                pair.openIdx, pair.closeIdx + 1);
        }

        // Bare identifier: walk left and right while characters are identifier-like.
        if (!IsIdentifierChar(line[idx])) return (string.Empty, idx, idx);
        var start = idx;
        while (start > 0 && IsIdentifierChar(line[start - 1])) start--;
        var end = idx;
        while (end < line.Length - 1 && IsIdentifierChar(line[end + 1])) end++;
        return (line.Substring(start, end - start + 1), start, end + 1);
    }

    private static (int openIdx, int closeIdx)? FindContainingQuotes(string line, int idx)
    {
        // Walk back to a '"'; if we cross a newline (we don't; per-line) or
        // another '"' first, no containment.
        var open = -1;
        for (var j = idx; j >= 0; j--)
        {
            if (line[j] == '"') { open = j; break; }
        }
        if (open < 0) return null;
        // The quote we found could be the closing quote of a string that
        // ends before idx. Check whether there's content between it and the
        // index; if idx == open we're sitting on a quote — still treat the
        // following identifier as the token.
        var close = -1;
        for (var j = Math.Max(open + 1, idx); j < line.Length; j++)
        {
            if (line[j] == '"') { close = j; break; }
        }
        if (close < 0) return null;
        if (idx > close) return null; // idx is past the closing quote
        return (open, close);
    }

    private static bool IsIdentifierChar(char c)
        => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Reads the meaningful characters immediately before <paramref name="wordStart"/>
    /// on the line. Used to decide whether the click landed on
    /// <c>X.Word</c> (qualified call), <c>Codeunit::"Word"</c> (typed
    /// reference), or <c>Codeunit "Word"</c> (object declaration / var).
    /// </summary>
    private static GoToDefinitionLeftContext ReadLeftContext(string line, int wordStart)
    {
        var i = wordStart - 1;
        while (i >= 0 && char.IsWhiteSpace(line[i])) i--;
        if (i < 0) return new GoToDefinitionLeftContext(null, null);

        // `.` operator — member access. Capture the qualifier identifier.
        if (line[i] == '.')
        {
            var qend = i - 1;
            while (qend >= 0 && char.IsWhiteSpace(line[qend])) qend--;
            if (qend < 0) return new GoToDefinitionLeftContext(".", null);
            // Quoted qualifier?
            if (line[qend] == '"')
            {
                var qopen = qend - 1;
                while (qopen >= 0 && line[qopen] != '"') qopen--;
                if (qopen < 0) return new GoToDefinitionLeftContext(".", null);
                var qualifier = line.Substring(qopen + 1, qend - qopen - 1);
                return new GoToDefinitionLeftContext(".", qualifier);
            }
            // Bare identifier qualifier.
            var qstart = qend;
            while (qstart > 0 && IsIdentifierChar(line[qstart - 1])) qstart--;
            return new GoToDefinitionLeftContext(".", line.Substring(qstart, qend - qstart + 1));
        }

        // `::` — typed reference like `Codeunit::"Sales-Post"`.
        if (i >= 1 && line[i] == ':' && line[i - 1] == ':')
        {
            var qend = i - 2;
            while (qend >= 0 && char.IsWhiteSpace(line[qend])) qend--;
            if (qend < 0) return new GoToDefinitionLeftContext("::", null);
            var qstart = qend;
            while (qstart > 0 && IsIdentifierChar(line[qstart - 1])) qstart--;
            var keyword = line.Substring(qstart, qend - qstart + 1);
            return new GoToDefinitionLeftContext("::",
                ObjectKeywords.Contains(keyword) ? keyword : null);
        }

        // Trailing identifier — possibly an object-reference keyword like
        // <c>Codeunit "Sales-Post"</c> (declaration / var typing).
        var kend = i;
        while (kend >= 0 && IsIdentifierChar(line[kend])) kend--;
        var kstart = kend + 1;
        var kendInclusive = i;
        var keywordCandidate = line.Substring(kstart, kendInclusive - kstart + 1);
        if (ObjectKeywords.Contains(keywordCandidate))
        {
            return new GoToDefinitionLeftContext("keyword", keywordCandidate);
        }

        return new GoToDefinitionLeftContext(null, null);
    }
}

/// <summary>
/// What <see cref="AlGoToDefinitionLocator.Inspect"/> found at the click
/// position. The caller combines <see cref="Word"/> and
/// <see cref="LeftContext"/> to pick the right symbol-table query.
/// </summary>
public sealed record GoToDefinitionClick(
    string Word,
    string LineText,
    GoToDefinitionLeftContext LeftContext);

/// <summary>
/// Context immediately to the left of the clicked token. <see cref="Operator"/>
/// is one of <c>.</c>, <c>::</c>, <c>keyword</c>, or <c>null</c> (nothing
/// meaningful). <see cref="Qualifier"/> is the identifier on the other side
/// of the operator (when applicable).
/// </summary>
public sealed record GoToDefinitionLeftContext(string? Operator, string? Qualifier);
