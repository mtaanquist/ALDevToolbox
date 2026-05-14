namespace ALDevToolbox.Services.Al;

/// <summary>
/// Scans an AL file for identifier tokens (bare or double-quoted) whose name
/// matches a known vocabulary of resolvable names — object names declared in
/// the version and procedure/event symbols callable from the file. The result
/// drives the "this name has a definition you can jump to" underline in the
/// Object Explorer file viewer.
///
/// Not a full parser. Comments and string literals are stripped (replaced
/// with spaces so column offsets stay stable) before tokenising. Comparisons
/// are case-insensitive because AL is case-insensitive.
/// </summary>
public static class AlResolvableTokenScanner
{
    /// <summary>
    /// Walks <paramref name="source"/> and emits one range per identifier
    /// whose unquoted name appears in <paramref name="vocabulary"/>. Ranges
    /// are 1-based and emitted in document order (ascending line then column).
    /// </summary>
    public static IReadOnlyList<ResolvableTokenRange> Scan(
        string source, IReadOnlySet<string> vocabulary)
    {
        if (string.IsNullOrEmpty(source) || vocabulary.Count == 0)
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
            ScanLine(stripped, li + 1, vocabulary, ranges);
        }

        return ranges;
    }

    private static void ScanLine(
        string lineText, int oneBasedLineNumber,
        IReadOnlySet<string> vocabulary, List<ResolvableTokenRange> ranges)
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
                if (name.Length > 0 && vocabulary.Contains(name))
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
                if (vocabulary.Contains(name))
                {
                    ranges.Add(new ResolvableTokenRange(
                        oneBasedLineNumber, start + 1, i + 1));
                }
                continue;
            }

            i++;
        }
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
/// One range that the file viewer should underline as resolvable. Columns are
/// 1-based; <see cref="ColumnEnd"/> is exclusive (matches the convention used
/// by <c>BaseAppSymbol</c>).
/// </summary>
public sealed record ResolvableTokenRange(int Line, int ColumnStart, int ColumnEnd);
