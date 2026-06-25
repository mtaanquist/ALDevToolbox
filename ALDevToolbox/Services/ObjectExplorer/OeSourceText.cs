namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Shared line-splitting for stored source blobs. Replaces the
/// <c>content.Replace("\r\n", "\n").Split('\n')</c> pattern that was repeated
/// across the source-viewer / search / reference services — that allocated two
/// full copies of each (potentially large) blob per call. Splitting on
/// <c>'\n'</c> and trimming a trailing <c>'\r'</c> only on the lines that have
/// one avoids the intermediate whole-string copy and normalises CRLF, CR-less
/// and mixed line endings the same way. See issue #387.
/// </summary>
internal static class OeSourceText
{
    /// <summary>
    /// Splits <paramref name="content"/> into lines, stripping a trailing
    /// <c>'\r'</c> from each so callers never see stray carriage returns.
    /// Returns an empty array for null/empty input.
    /// </summary>
    public static string[] SplitLines(string? content)
    {
        if (string.IsNullOrEmpty(content)) return [];
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].EndsWith('\r')) lines[i] = lines[i][..^1];
        }
        return lines;
    }
}
