namespace ALDevToolbox.Services.Cookbook;

/// <summary>
/// Lightweight line-oriented AL tokenizer for read-only display of recipe
/// source in the Cookbook recipe-detail view. A faithful C# port of the
/// prototype highlighter in
/// <c>.design/explorer-cookbook/app/screen-cookbook.jsx</c> (<c>alTokens</c>).
///
/// It is intentionally simple — keyword / type / string / identifier / number /
/// comment classification, no full AL grammar. The token text is emitted as
/// plain strings; Razor HTML-encodes it at render time, so this never produces
/// markup. A richer grammar (the editor's CodeMirror tokenizer) is a possible
/// future upgrade; this keeps the recipe view self-contained.
/// </summary>
public static class AlSyntaxHighlighter
{
    /// <summary>
    /// One highlighted run within a line. <see cref="Cls"/> is the CSS class the
    /// run is rendered with, from the design system's static-code vocabulary:
    /// <c>k</c> keyword, <c>t</c> type, <c>s</c> string, <c>o</c> object name,
    /// <c>n</c> number, <c>c</c> comment — the six <c>.code-block pre</c> and
    /// <c>.codev</c> both define. <b>Empty</b> for punctuation and plain text,
    /// which carry no class and inherit the block's own colour; the caller is
    /// expected to emit those as bare text rather than an empty span.
    ///
    /// <para>Until #587 these were a private <c>tok-*</c> family sharing a
    /// prefix with CodeMirror's lezer tag classes — a third way to tint AL, in
    /// an app that only ever wanted one.</para>
    /// </summary>
    public readonly record struct Token(string Cls, string Text);

    /// <summary>Punctuation and unclassified words: no class, no span.</summary>
    private const string Plain = "";

    // Matched case-insensitively (mirrors AL_KW.has(w.toLowerCase())).
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "codeunit", "page", "pageextension", "tableextension", "table", "report", "enum",
        "local", "internal", "protected", "procedure", "trigger", "var", "begin", "end",
        "if", "then", "else", "exit", "repeat", "until", "while", "do", "case", "of",
        "layout", "actions", "addlast", "addfirst", "addafter", "addbefore", "modify",
        "extends", "implements", "true", "false", "or", "and", "not", "div", "mod", "in",
    };

    // Matched case-sensitively (mirrors AL_TYPE.has(w)).
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "Record", "Boolean", "Integer", "Text", "Code", "Decimal", "BigInteger", "Date",
        "DateTime", "Guid", "Option", "Variant", "RecordRef", "FieldRef",
        "Page", "Codeunit", "ObjectType", "ApplicationArea", "ToolTip", "Caption",
        "Editable", "Locked", "SourceExpr", "Visible",
    };

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Tokenizes a single line of AL source into classified runs.</summary>
    public static List<Token> TokenizeLine(string line)
    {
        var outTokens = new List<Token>();
        var i = 0;
        var n = line.Length;

        while (i < n)
        {
            var c = line[i];

            // // line comment — rest of the line.
            if (c == '/' && i + 1 < n && line[i + 1] == '/')
            {
                outTokens.Add(new Token("c", line[i..]));
                break;
            }

            // 'single-quoted string'
            if (c == '\'')
            {
                var j = i + 1;
                while (j < n && line[j] != '\'') j++;
                j = Math.Min(j + 1, n);
                outTokens.Add(new Token("s", line[i..j]));
                i = j;
                continue;
            }

            // "double-quoted AL identifier"
            if (c == '"')
            {
                var j = i + 1;
                while (j < n && line[j] != '"') j++;
                j = Math.Min(j + 1, n);
                outTokens.Add(new Token("o", line[i..j]));
                i = j;
                continue;
            }

            // word: number / keyword / type / plain text
            if (IsWord(c))
            {
                var j = i;
                while (j < n && IsWord(line[j])) j++;
                var w = line[i..j];
                var t = Plain;
                if (char.IsDigit(w[0])) t = "n";
                else if (Keywords.Contains(w)) t = "k";
                else if (Types.Contains(w)) t = "t";
                outTokens.Add(new Token(t, w));
                i = j;
                continue;
            }

            // run of punctuation / whitespace (stops at a word/quote/comment start)
            var k = i;
            while (k < n
                   && !IsWord(line[k])
                   && line[k] != '\''
                   && line[k] != '"'
                   && !(line[k] == '/' && k + 1 < n && line[k + 1] == '/'))
            {
                k++;
            }
            outTokens.Add(new Token(Plain, line[i..k]));
            i = k;
        }

        return outTokens;
    }
}
