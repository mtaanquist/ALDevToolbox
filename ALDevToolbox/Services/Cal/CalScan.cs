namespace ALDevToolbox.Services.Cal;

/// <summary>
/// Low-level text scanning helpers shared by <see cref="CalObjectSplitter"/>
/// and <see cref="CalObjectParser"/>. C/AL TXT is brace-structured, but braces
/// can sit next to single-quoted string literals (<c>'…'</c> with <c>''</c>
/// escaping), double-quoted identifiers (<c>"Sales Header"</c>), and <c>//</c>
/// line comments. These helpers count structure while skipping those.
///
/// <para>
/// Crucially, a quote only opens a span when it has a matching close <b>on the
/// same line</b> (see <see cref="QuoteSpanEnd"/>). C/AL string literals and
/// quoted identifiers never span lines, and apostrophes appear as literal
/// characters inside unquoted field names and captions (<c>Relative's Employee
/// No.</c>, <c>[DAN=…]</c>). Treating a lone <c>'</c> as a string start would
/// swallow the rest of the line — including the closing brace — and merge
/// objects together.
/// </para>
/// </summary>
internal static class CalScan
{
    /// <summary>1-based line number of <paramref name="offset"/> within <paramref name="text"/>.</summary>
    public static int LineAt(string text, int offset)
    {
        int n = 1;
        int end = Math.Min(offset, text.Length);
        for (int i = 0; i < end; i++)
            if (text[i] == '\n') n++;
        return n;
    }

    /// <summary>1-based column of <paramref name="offset"/> within its line.</summary>
    public static int ColumnAt(string text, int offset)
    {
        int col = 1;
        int end = Math.Min(offset, text.Length);
        for (int i = 0; i < end; i++)
            col = text[i] == '\n' ? 1 : col + 1;
        return col;
    }

    /// <summary>
    /// If <c>text[i]</c> (a <c>'</c> or <c>"</c>) opens a quoted span that closes
    /// on the same line, returns the index of the closing quote. Otherwise
    /// returns <paramref name="i"/> — the quote is a literal character (a
    /// possessive apostrophe or stray quote), and the caller should advance by
    /// one. <c>''</c> escapes a quote inside a <c>'…'</c> string.
    /// </summary>
    public static int QuoteSpanEnd(string text, int i)
    {
        char q = text[i];
        for (int j = i + 1; j < text.Length; j++)
        {
            char c = text[j];
            if (c == '\n') return i;            // no close on this line → literal
            if (c == q)
            {
                if (q == '\'' && j + 1 < text.Length && text[j + 1] == '\'') { j++; continue; }
                return j;
            }
        }
        return i;
    }

    /// <summary>
    /// Given the index of an opening <c>{</c>, returns the index of its matching
    /// <c>}</c>, or -1 if unbalanced. Skips quoted spans, <c>//</c> comments, and
    /// <c>[…]</c> caption/tooltip brackets — the latter are opaque multi-line
    /// text that can hold URLs (whose <c>//</c> is not a comment), apostrophes,
    /// and stray braces, none of which are structure.
    /// </summary>
    public static int FindMatchingBrace(string text, int openIndex)
    {
        int depth = 0, bracket = 0;
        for (int i = openIndex; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\'' || c == '"') { int e = QuoteSpanEnd(text, i); if (e != i) i = e; continue; }
            if (bracket == 0 && c == '/' && i + 1 < text.Length && text[i + 1] == '/') { i = SkipLineComment(text, i); continue; }
            if (c == '[') { bracket++; continue; }
            if (c == ']') { if (bracket > 0) bracket--; continue; }
            if (bracket > 0) continue;
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Given the index of a <c>(</c>, returns the index of its matching <c>)</c>,
    /// or -1 if unbalanced. Skips quoted spans.
    /// </summary>
    public static int FindMatchingParen(string text, int openIndex)
    {
        int depth = 0;
        for (int i = openIndex; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\'' || c == '"') { int e = QuoteSpanEnd(text, i); if (e != i) i = e; continue; }
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Given the index of a <c>BEGIN</c> keyword, returns the index just past the
    /// matching <c>END</c> token, or -1 if unbalanced. Counts <c>BEGIN</c> and
    /// <c>CASE</c> as openers and <c>END</c> as closers (the two C/AL block
    /// constructs that close with <c>END</c>), skipping quoted spans, <c>//</c>
    /// comments, and <c>{…}</c> brace comments. Case-insensitive, whole-word.
    /// </summary>
    public static int FindMatchingEnd(string text, int beginIndex)
    {
        int depth = 0;
        int i = beginIndex;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\'' || c == '"') { int e = QuoteSpanEnd(text, i); i = e == i ? i + 1 : e + 1; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { i = SkipLineComment(text, i) + 1; continue; }
            if (c == '{') { int close = FindMatchingBrace(text, i); i = close < 0 ? text.Length : close + 1; continue; }

            if (IsWordStart(c) && IsWordBoundaryBefore(text, i))
            {
                int wordEnd = i;
                while (wordEnd < text.Length && IsWordChar(text[wordEnd])) wordEnd++;
                var word = text.AsSpan(i, wordEnd - i);
                if (word.Equals("BEGIN", StringComparison.OrdinalIgnoreCase)
                    || word.Equals("CASE", StringComparison.OrdinalIgnoreCase))
                {
                    depth++;
                }
                else if (word.Equals("END", StringComparison.OrdinalIgnoreCase))
                {
                    depth--;
                    if (depth == 0) return wordEnd;
                }
                i = wordEnd;
                continue;
            }
            i++;
        }
        return -1;
    }

    /// <summary>
    /// Splits <paramref name="text"/> on top-level <c>;</c> separators, treating
    /// quoted spans, <c>[…]</c> brackets, parens, and <c>BEGIN…END</c> blocks as
    /// opaque. Used to peel the positional columns off a FIELDS record and to
    /// separate variable declarations.
    /// </summary>
    public static List<(int Start, int End)> SplitTopLevelSemicolons(string text, int start, int end)
    {
        var parts = new List<(int, int)>();
        int partStart = start;
        int i = start;
        int bracket = 0, paren = 0, beginDepth = 0;
        while (i < end)
        {
            char c = text[i];
            if (c == '\'' || c == '"') { int e = QuoteSpanEnd(text, i); i = e == i ? i + 1 : e + 1; continue; }
            if (bracket == 0 && c == '/' && i + 1 < end && text[i + 1] == '/') { i = SkipLineComment(text, i) + 1; continue; }
            if (c == '[') { bracket++; i++; continue; }
            if (c == ']') { if (bracket > 0) bracket--; i++; continue; }
            if (c == '(') { paren++; i++; continue; }
            if (c == ')') { if (paren > 0) paren--; i++; continue; }
            if (IsWordStart(c) && (i == 0 || !IsWordChar(text[i - 1])))
            {
                int we = i;
                while (we < end && IsWordChar(text[we])) we++;
                var w = text.AsSpan(i, we - i);
                if (w.Equals("BEGIN", StringComparison.OrdinalIgnoreCase) || w.Equals("CASE", StringComparison.OrdinalIgnoreCase)) beginDepth++;
                else if (w.Equals("END", StringComparison.OrdinalIgnoreCase) && beginDepth > 0) beginDepth--;
                i = we;
                continue;
            }
            if (c == ';' && bracket == 0 && paren == 0 && beginDepth == 0)
            {
                parts.Add((partStart, i));
                partStart = i + 1;
            }
            i++;
        }
        if (partStart < end) parts.Add((partStart, end));
        return parts;
    }

    /// <summary>Index of the last char before the newline that ends a <c>//</c> comment.</summary>
    public static int SkipLineComment(string text, int slashIndex)
    {
        int i = slashIndex;
        while (i < text.Length && text[i] != '\n') i++;
        return i - 1;
    }

    public static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';
    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsWordBoundaryBefore(string text, int i)
        => i == 0 || !IsWordChar(text[i - 1]);
}
