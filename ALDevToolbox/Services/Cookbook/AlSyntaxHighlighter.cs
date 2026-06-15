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
    /// <summary>One highlighted run within a line. <see cref="Cls"/> is the
    /// CSS token suffix, e.g. "kw" → rendered as <c>tok-kw</c>.</summary>
    public readonly record struct Token(string Cls, string Text);

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
                outTokens.Add(new Token("com", line[i..]));
                break;
            }

            // 'single-quoted string'
            if (c == '\'')
            {
                var j = i + 1;
                while (j < n && line[j] != '\'') j++;
                j = Math.Min(j + 1, n);
                outTokens.Add(new Token("str", line[i..j]));
                i = j;
                continue;
            }

            // "double-quoted AL identifier"
            if (c == '"')
            {
                var j = i + 1;
                while (j < n && line[j] != '"') j++;
                j = Math.Min(j + 1, n);
                outTokens.Add(new Token("id", line[i..j]));
                i = j;
                continue;
            }

            // word: number / keyword / type / plain text
            if (IsWord(c))
            {
                var j = i;
                while (j < n && IsWord(line[j])) j++;
                var w = line[i..j];
                var t = "txt";
                if (char.IsDigit(w[0])) t = "num";
                else if (Keywords.Contains(w)) t = "kw";
                else if (Types.Contains(w)) t = "type";
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
            outTokens.Add(new Token("punc", line[i..k]));
            i = k;
        }

        return outTokens;
    }
}
