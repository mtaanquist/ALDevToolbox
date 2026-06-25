using System.Text;
using System.Text.RegularExpressions;

namespace ALDevToolbox.Services.Cal;

/// <summary>
/// Streams a legacy C/AL TXT export and yields one <see cref="CalObjectBlock"/>
/// per <c>OBJECT &lt;Type&gt; &lt;Id&gt; &lt;Name&gt; { … }</c> declaration. Only
/// one object's text is buffered at a time, so a 150 MB file (the typical full
/// NAV database export) never materialises in memory — the same per-object
/// bounded-memory philosophy the AL <c>.app</c> path uses.
///
/// Brace counting skips <c>'…'</c> strings and <c>//</c> comments (see
/// <see cref="CalScan"/>) so a stray brace in a TextConst or comment doesn't
/// mis-split an object. A truncated final object is logged and dropped rather
/// than aborting the thousands that parsed cleanly.
/// </summary>
public sealed partial class CalObjectSplitter
{
    [GeneratedRegex(@"^OBJECT\s+(?<type>[A-Za-z]+)\s+(?<id>\d+)\s+(?<name>.*?)\s*$")]
    private static partial Regex ObjectHeaderRegex();

    /// <summary>
    /// Reads <paramref name="stream"/> with <paramref name="encoding"/> (no BOM
    /// detection — C/AL TXT is raw Windows-1252 or codepage 850) and yields each
    /// object block. <paramref name="onWarning"/>, when supplied, is called for a
    /// dangling/truncated trailing object.
    /// </summary>
    public static IEnumerable<CalObjectBlock> Split(
        Stream stream, Encoding encoding, Action<string>? onWarning = null)
    {
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false);

        var buffer = new StringBuilder();
        int braceDepth = 0;
        int bracketDepth = 0;       // [..] caption/tooltip nesting, carried across lines
        bool inObject = false;
        int fileLine = 0;          // 1-based line counter over the whole file
        int objectStartLine = 0;
        string? headerType = null;
        int headerId = 0;
        string headerName = string.Empty;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            fileLine++;

            if (!inObject)
            {
                var m = ObjectHeaderRegex().Match(line);
                if (!m.Success) continue;   // skip blank lines / stray text between objects
                // A corrupt / oversized id line (e.g. `OBJECT Table 99999999999 …`)
                // overflows int.Parse — which would abort the whole file and lose
                // the thousands of clean objects after it. Warn and skip just this
                // object instead, matching ParseFields' tolerant handling. See #368.
                if (!int.TryParse(m.Groups["id"].Value, out var parsedId))
                {
                    onWarning?.Invoke(
                        $"Skipping object at line {fileLine}: unparseable id '{m.Groups["id"].Value}'.");
                    continue;
                }
                inObject = true;
                braceDepth = 0;
                bracketDepth = 0;
                objectStartLine = fileLine;
                headerType = m.Groups["type"].Value;
                headerId = parsedId;
                headerName = m.Groups["name"].Value;
                buffer.Clear();
                buffer.Append(line).Append('\n');
                continue;
            }

            buffer.Append(line).Append('\n');
            braceDepth += CountBraceDelta(line, ref bracketDepth);

            // The object body is opened by the '{' on the line after the header
            // and closed when depth returns to 0. A header with no body (depth
            // never rises) is impossible in valid C/AL, so only close on a
            // genuine return-to-zero after the body opened.
            if (braceDepth <= 0 && buffer.ToString().Contains('{'))
            {
                yield return new CalObjectBlock(
                    headerType!, headerId, headerName, objectStartLine, buffer.ToString());
                inObject = false;
                headerType = null;
            }
        }

        if (inObject)
            onWarning?.Invoke($"Truncated C/AL object '{headerType} {headerId} {headerName}' at line {objectStartLine}; skipped.");
    }

    /// <summary>
    /// Net structural <c>{</c>-minus-<c>}</c> over one line. Braces are only
    /// counted at <paramref name="bracketDepth"/> 0 (outside <c>[…]</c>
    /// captions/tooltips, which carry across lines and can hold URLs, stray
    /// braces, and apostrophes). A quote opens a span only when it closes on the
    /// same line — a lone <c>'</c> (a possessive like <c>Relative's Employee
    /// No.</c>) is a literal. A <c>//</c> comment is only honoured outside
    /// brackets, so the <c>//</c> in a URL inside a caption isn't a comment.
    /// </summary>
    private static int CountBraceDelta(string line, ref int bracketDepth)
    {
        int delta = 0;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\'' || c == '"')
            {
                int e = CalScan.QuoteSpanEnd(line, i);
                if (e != i) i = e;              // skip the closed quoted span
                continue;                        // else treat as a literal char
            }
            if (bracketDepth == 0 && c == '/' && i + 1 < line.Length && line[i + 1] == '/') break; // comment
            if (c == '[') { bracketDepth++; continue; }
            if (c == ']') { if (bracketDepth > 0) bracketDepth--; continue; }
            if (bracketDepth > 0) continue;
            if (c == '{') delta++;
            else if (c == '}') delta--;
        }
        return delta;
    }
}
