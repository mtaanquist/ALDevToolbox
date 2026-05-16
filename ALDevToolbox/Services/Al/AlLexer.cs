using System.Collections.Generic;
using System.Text;

namespace ALDevToolbox.Services.Al;

/// <summary>
/// Token kinds produced by <see cref="AlLexer"/>. The lexer is positional:
/// every token carries a 1-based line + column so downstream passes can
/// emit reference rows that point back at the exact source position.
///
/// Whitespace and comments are consumed by the lexer (they don't appear
/// in the token stream) but their newlines feed the line counter so the
/// surviving tokens still reference the source they came from.
///
/// AL is largely Pascal-shaped:
/// <list type="bullet">
///   <item>Identifiers — bare (<c>SalesLine</c>) or double-quoted
///         (<c>"Sales Line"</c>, dashes / spaces allowed inside).</item>
///   <item>String literals — single-quoted, doubled single quote escapes
///         the quote (<c>'it''s'</c>).</item>
///   <item>Numbers — integer + decimal; we don't bother with full numeric
///         syntax (the consumer never inspects values).</item>
///   <item>Multi-char operators — <c>::</c>, <c>:=</c>, <c>..</c>,
///         <c>&lt;=</c>, <c>&gt;=</c>, <c>&lt;&gt;</c>.</item>
///   <item>Pragma / directive — <c>#pragma warning disable AA0074</c>,
///         <c>#region ...</c>. Treated as a single token swallowing the
///         rest of the line; downstream passes don't look inside.</item>
/// </list>
/// </summary>
public enum AlTokenKind
{
    /// <summary>Bare identifier — <c>SalesLine</c>, <c>Insert</c>, <c>Validate</c>.</summary>
    Identifier,

    /// <summary>Quoted identifier — <c>"Sales Line"</c>, <c>"No."</c>.</summary>
    QuotedIdentifier,

    /// <summary>Single-quoted string literal — <c>'hello'</c>.</summary>
    String,

    /// <summary>Numeric literal — <c>42</c>, <c>3.14</c>.</summary>
    Number,

    /// <summary>Single punctuation char — <c>.</c>, <c>,</c>, <c>;</c>, <c>(</c>, <c>)</c>, <c>[</c>, <c>]</c>, <c>{</c>, <c>}</c>, <c>=</c>, <c>+</c>, <c>-</c>, <c>*</c>, <c>/</c>, <c>:</c>, <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, <c>|</c>, <c>!</c>, <c>?</c>, <c>@</c>.</summary>
    Punct,

    /// <summary><c>::</c> — typed reference (<c>Codeunit::"Sales-Post"</c>).</summary>
    DoubleColon,

    /// <summary><c>:=</c> — assignment.</summary>
    Assign,

    /// <summary><c>+=</c>, <c>-=</c>, <c>*=</c>, <c>/=</c> — compound assignment.</summary>
    CompoundAssign,

    /// <summary><c>..</c> — range.</summary>
    Range,

    /// <summary><c>&lt;=</c>, <c>&gt;=</c>, <c>&lt;&gt;</c> — comparison.</summary>
    Compare,

    /// <summary>
    /// Pragma / region directive consuming the rest of the line —
    /// <c>#pragma warning disable AA0074</c>. We don't inspect the
    /// contents but emit one token so the consumer can advance past it.
    /// </summary>
    Directive,
}

/// <summary>
/// One token emitted by <see cref="AlLexer"/>. Coordinates are 1-based,
/// matching the rest of the import pipeline.
/// </summary>
/// <param name="Kind">What category of token this is.</param>
/// <param name="Value">
/// The token's source text. For quoted identifiers the surrounding
/// quotes are stripped (<c>"Sales Line"</c> → <c>Sales Line</c>) so the
/// consumer doesn't have to peel them. For strings the surrounding
/// quotes ARE preserved so the consumer can tell the kind apart from
/// an identifier with the same characters.
/// </param>
/// <param name="Line">1-based source line where the token begins.</param>
/// <param name="Column">1-based source column where the token begins.</param>
public readonly record struct AlToken(AlTokenKind Kind, string Value, int Line, int Column);

/// <summary>
/// Single-pass lexer for AL source. Produces a list of tokens for
/// downstream consumers (the reference extractor, the scope tracker).
/// Pure function — no DB, no IO, no state beyond the cursor.
///
/// Not a full parser: it doesn't know AL keywords or grammar. It just
/// segments the source into the categories downstream passes care
/// about (identifiers, member-access operators, parens, strings)
/// while skipping comments and whitespace.
/// </summary>
public static class AlLexer
{
    /// <summary>
    /// Tokenises an AL source string. Comments and whitespace are
    /// consumed; their newlines still advance the line counter so
    /// downstream coordinates are correct.
    /// </summary>
    public static List<AlToken> Tokenize(string source)
    {
        var tokens = new List<AlToken>();
        if (string.IsNullOrEmpty(source)) return tokens;

        var line = 1;
        var col = 1;
        var i = 0;
        var n = source.Length;

        while (i < n)
        {
            var c = source[i];

            // Whitespace — track newlines, otherwise just advance.
            if (c == '\r')
            {
                // Treat CR / CRLF / LF uniformly: count once at LF (or here for bare CR).
                if (i + 1 < n && source[i + 1] == '\n')
                {
                    i++;
                }
                line++;
                col = 1;
                i++;
                continue;
            }
            if (c == '\n')
            {
                line++;
                col = 1;
                i++;
                continue;
            }
            if (c == ' ' || c == '\t')
            {
                col++;
                i++;
                continue;
            }

            // Line comment // ... → end of line.
            if (c == '/' && i + 1 < n && source[i + 1] == '/')
            {
                while (i < n && source[i] != '\n' && source[i] != '\r') i++;
                continue;
            }

            // Block comment /* ... */ — handle nesting? AL doesn't nest,
            // so single-level scan to the matching */.
            if (c == '/' && i + 1 < n && source[i + 1] == '*')
            {
                i += 2;
                col += 2;
                while (i + 1 < n && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    if (source[i] == '\n') { line++; col = 1; }
                    else if (source[i] == '\r')
                    {
                        if (i + 1 < n && source[i + 1] == '\n') i++;
                        line++; col = 1;
                    }
                    else col++;
                    i++;
                }
                // Skip past the closing */.
                if (i + 1 < n) { i += 2; col += 2; }
                else { i = n; }
                continue;
            }

            // Pragma / directive — # to end of line. Single token so the
            // consumer's index advances cleanly.
            if (c == '#')
            {
                var startCol = col;
                var startLine = line;
                var sb = new StringBuilder();
                while (i < n && source[i] != '\n' && source[i] != '\r')
                {
                    sb.Append(source[i]);
                    i++;
                    col++;
                }
                tokens.Add(new AlToken(AlTokenKind.Directive, sb.ToString(), startLine, startCol));
                continue;
            }

            // Quoted identifier "Sales Line" — close on next unescaped ".
            // AL doesn't double the inner quote (unlike single-quoted strings)
            // because identifiers can't contain a literal ".
            if (c == '"')
            {
                var startCol = col;
                var startLine = line;
                i++;
                col++;
                var sb = new StringBuilder();
                while (i < n && source[i] != '"')
                {
                    if (source[i] == '\n')
                    {
                        // Unterminated quoted identifier — bail by treating
                        // the rest as part of the identifier and resyncing
                        // at the newline. Source code with unterminated
                        // identifiers wouldn't compile anyway.
                        break;
                    }
                    sb.Append(source[i]);
                    col++;
                    i++;
                }
                if (i < n && source[i] == '"') { i++; col++; }
                tokens.Add(new AlToken(AlTokenKind.QuotedIdentifier, sb.ToString(), startLine, startCol));
                continue;
            }

            // String literal 'text'. Doubled single quote escapes ('it''s').
            if (c == '\'')
            {
                var startCol = col;
                var startLine = line;
                var sb = new StringBuilder();
                sb.Append('\'');
                i++;
                col++;
                while (i < n)
                {
                    if (source[i] == '\'')
                    {
                        if (i + 1 < n && source[i + 1] == '\'')
                        {
                            sb.Append("''");
                            i += 2;
                            col += 2;
                            continue;
                        }
                        sb.Append('\'');
                        i++;
                        col++;
                        break;
                    }
                    if (source[i] == '\n') { line++; col = 1; }
                    else col++;
                    sb.Append(source[i]);
                    i++;
                }
                tokens.Add(new AlToken(AlTokenKind.String, sb.ToString(), startLine, startCol));
                continue;
            }

            // Identifier — letter or underscore followed by letters/digits/underscores.
            if (IsIdentifierStart(c))
            {
                var startCol = col;
                var startLine = line;
                var sb = new StringBuilder();
                while (i < n && IsIdentifierPart(source[i]))
                {
                    sb.Append(source[i]);
                    i++;
                    col++;
                }
                tokens.Add(new AlToken(AlTokenKind.Identifier, sb.ToString(), startLine, startCol));
                continue;
            }

            // Number — full lexical shape per Microsoft's AL grammar:
            //   * Hex literal: 0x[0-9a-fA-F]+
            //   * Decimal: [0-9]+ (. [0-9]+)? ([eE] [+-]? [0-9]+)?
            //   * Optional suffix: L / U / F (case-insensitive, up to 3
            //     chars — covers L, U, F, LL, UL, ULL).
            // AL doesn't have a date-literal syntax of its own; dates
            // are constructed via DMY2Date() etc.
            if (c >= '0' && c <= '9')
            {
                var startCol = col;
                var startLine = line;
                var sb = new StringBuilder();

                // Hex literal: 0x... — must have at least one hex digit
                // after the prefix, otherwise we treat `0` as a plain
                // number and let the `x...` lex as an identifier.
                if (c == '0' && i + 1 < n
                    && (source[i + 1] == 'x' || source[i + 1] == 'X')
                    && i + 2 < n && IsHexDigit(source[i + 2]))
                {
                    sb.Append(source[i]);
                    sb.Append(source[i + 1]);
                    i += 2; col += 2;
                    while (i < n && IsHexDigit(source[i]))
                    {
                        sb.Append(source[i]);
                        i++; col++;
                    }
                }
                else
                {
                    // Integer part.
                    while (i < n && source[i] >= '0' && source[i] <= '9')
                    {
                        sb.Append(source[i]);
                        i++; col++;
                    }
                    // Decimal part — require a digit after the dot so
                    // member access like `42.ToString` doesn't get
                    // mis-read as a malformed decimal.
                    if (i < n && source[i] == '.'
                        && i + 1 < n && source[i + 1] >= '0' && source[i + 1] <= '9')
                    {
                        sb.Append('.');
                        i++; col++;
                        while (i < n && source[i] >= '0' && source[i] <= '9')
                        {
                            sb.Append(source[i]);
                            i++; col++;
                        }
                    }
                    // Scientific exponent. Only commit when the e/E is
                    // followed by an optional sign and at least one
                    // digit — otherwise `1eY` would partially-consume
                    // and leave the lexer mid-token.
                    if (i < n && (source[i] == 'e' || source[i] == 'E'))
                    {
                        var look = i + 1;
                        if (look < n && (source[look] == '+' || source[look] == '-')) look++;
                        if (look < n && source[look] >= '0' && source[look] <= '9')
                        {
                            sb.Append(source[i]);
                            i++; col++;
                            if (i < n && (source[i] == '+' || source[i] == '-'))
                            {
                                sb.Append(source[i]);
                                i++; col++;
                            }
                            while (i < n && source[i] >= '0' && source[i] <= '9')
                            {
                                sb.Append(source[i]);
                                i++; col++;
                            }
                        }
                    }
                }

                // Numeric type suffix: L / U / F (case-insensitive).
                // MS grammar accepts L, U, F, LL, UL, ULL — we cap at 3
                // chars so `100Llong` doesn't swallow the next identifier.
                int suffixCount = 0;
                while (suffixCount < 3 && i < n && IsNumericSuffixChar(source[i]))
                {
                    sb.Append(source[i]);
                    i++; col++;
                    suffixCount++;
                }

                tokens.Add(new AlToken(AlTokenKind.Number, sb.ToString(), startLine, startCol));
                continue;
            }

            // Multi-char punctuation. Order matters: longer first.
            //
            // Set: `::` `:=` `..` `<=` `>=` `<>` `+=` `-=` `*=` `/=`.
            // Confirmed against Microsoft's AL TextMate grammar
            // (microsoft/AL grammar/alsyntax.tmlanguage).
            //
            // `/=` must come AFTER the `//` and `/*` comment checks
            // above; those run earlier in the loop so we can't reach
            // here with a `/=` that's actually a comment start.

            // Compound assignment: += -= *= /=.
            if (i + 1 < n && source[i + 1] == '='
                && (c == '+' || c == '-' || c == '*' || c == '/'))
            {
                var op = string.Concat(c.ToString(), "=");
                tokens.Add(new AlToken(AlTokenKind.CompoundAssign, op, line, col));
                i += 2; col += 2;
                continue;
            }

            if (c == ':' && i + 1 < n && source[i + 1] == ':')
            {
                tokens.Add(new AlToken(AlTokenKind.DoubleColon, "::", line, col));
                i += 2; col += 2;
                continue;
            }
            if (c == ':' && i + 1 < n && source[i + 1] == '=')
            {
                tokens.Add(new AlToken(AlTokenKind.Assign, ":=", line, col));
                i += 2; col += 2;
                continue;
            }
            if (c == '.' && i + 1 < n && source[i + 1] == '.')
            {
                tokens.Add(new AlToken(AlTokenKind.Range, "..", line, col));
                i += 2; col += 2;
                continue;
            }
            if ((c == '<' || c == '>') && i + 1 < n && (source[i + 1] == '=' || (c == '<' && source[i + 1] == '>')))
            {
                var op = c.ToString() + source[i + 1];
                tokens.Add(new AlToken(AlTokenKind.Compare, op, line, col));
                i += 2; col += 2;
                continue;
            }

            // Single-char punctuation. Catch-all for any other byte we
            // don't know what to do with — the consumer can ignore tokens
            // it doesn't recognise.
            tokens.Add(new AlToken(AlTokenKind.Punct, c.ToString(), line, col));
            i++;
            col++;
        }

        return tokens;
    }

    private static bool IsIdentifierStart(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';

    private static bool IsIdentifierPart(char c) =>
        IsIdentifierStart(c) || (c >= '0' && c <= '9');

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static bool IsNumericSuffixChar(char c) =>
        c == 'L' || c == 'l' || c == 'U' || c == 'u' || c == 'F' || c == 'f';
}
