namespace ALDevToolbox.Services.Cal;

public enum CalTokenKind
{
    Identifier,        // bare identifier (keyword or name; case-insensitive)
    QuotedIdentifier,  // "No." — a quoted field/object name
    Number,
    Dot,               // .
    LParen,            // (
    RParen,            // )
    LBracket,          // [
    RBracket,          // ]
    ColonColon,        // :: (option/enum or DATABASE::)
    Comma,
    Semicolon,
    Other,             // any operator/punctuation we don't model
}

public readonly record struct CalToken(CalTokenKind Kind, string Text, int Line, int Column);

/// <summary>
/// Tokenises a C/AL procedure / trigger body for <see cref="CalReferenceExtractor"/>.
/// C/AL bodies reference variables and members by bare name (the <c>@id</c>
/// element-id suffix only appears in declarations, never in code), so this is a
/// straightforward Pascal-shaped lexer: <c>'…'</c> strings (<c>''</c> escapes),
/// <c>"…"</c> quoted identifiers, <c>//</c> and <c>{…}</c> comments, numbers,
/// and the handful of punctuation tokens the walker keys on (<c>.</c> <c>(</c>
/// <c>)</c> <c>::</c>). Keywords are returned as <see cref="CalTokenKind.Identifier"/>
/// and filtered by the walker, matching the case-insensitive AL lexer's shape.
/// 1-based line/column, slice-relative.
/// </summary>
public static class CalLexer
{
    public static List<CalToken> Tokenize(string text)
    {
        var tokens = new List<CalToken>();
        int line = 1, col = 1, i = 0, n = text.Length;

        void Advance(int count = 1)
        {
            for (int k = 0; k < count && i < n; k++)
            {
                if (text[i] == '\n') { line++; col = 1; }
                else col++;
                i++;
            }
        }

        while (i < n)
        {
            char c = text[i];

            if (c == '\n' || c == '\r' || c == ' ' || c == '\t') { Advance(); continue; }

            // // line comment
            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                while (i < n && text[i] != '\n') Advance();
                continue;
            }
            // { } brace comment (rare inside bodies, but legal)
            if (c == '{')
            {
                while (i < n && text[i] != '}') Advance();
                if (i < n) Advance();
                continue;
            }
            // ' single-quoted string ('' escapes)
            if (c == '\'')
            {
                Advance();
                while (i < n)
                {
                    if (text[i] == '\'')
                    {
                        if (i + 1 < n && text[i + 1] == '\'') { Advance(2); continue; }
                        Advance();
                        break;
                    }
                    Advance();
                }
                continue;
            }
            // " quoted identifier
            if (c == '"')
            {
                int startLine = line, startCol = col, start = i;
                Advance();
                while (i < n && text[i] != '"') Advance();
                string name = text.Substring(start + 1, Math.Max(0, i - start - 1));
                if (i < n) Advance(); // past closing "
                tokens.Add(new CalToken(CalTokenKind.QuotedIdentifier, name, startLine, startCol));
                continue;
            }

            if (CalScan.IsWordStart(c))
            {
                int startLine = line, startCol = col, start = i;
                while (i < n && CalScan.IsWordChar(text[i])) Advance();
                tokens.Add(new CalToken(CalTokenKind.Identifier, text[start..i], startLine, startCol));
                continue;
            }

            if (char.IsDigit(c))
            {
                int startLine = line, startCol = col, start = i;
                // Only consume a '.' that's part of the number (followed by a
                // digit). A trailing dot is member access (5.Field) or a range
                // (1..10), not part of the literal — leave it for the lexer to
                // emit as Dot.
                while (i < n && (char.IsDigit(text[i])
                    || (text[i] == '.' && i + 1 < n && char.IsDigit(text[i + 1])))) Advance();
                tokens.Add(new CalToken(CalTokenKind.Number, text[start..i], startLine, startCol));
                continue;
            }

            // Punctuation
            switch (c)
            {
                case '.': Emit(CalTokenKind.Dot); break;
                case '(': Emit(CalTokenKind.LParen); break;
                case ')': Emit(CalTokenKind.RParen); break;
                case '[': Emit(CalTokenKind.LBracket); break;
                case ']': Emit(CalTokenKind.RBracket); break;
                case ',': Emit(CalTokenKind.Comma); break;
                case ';': Emit(CalTokenKind.Semicolon); break;
                case ':':
                    if (i + 1 < n && text[i + 1] == ':') { tokens.Add(new CalToken(CalTokenKind.ColonColon, "::", line, col)); Advance(2); continue; }
                    Emit(CalTokenKind.Other); break;
                default: Emit(CalTokenKind.Other); break;
            }
            continue;

            void Emit(CalTokenKind k) { tokens.Add(new CalToken(k, text[i].ToString(), line, col)); Advance(); }
        }

        return tokens;
    }
}
