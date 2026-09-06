namespace ALDevToolbox.Services.Cookbook;

/// <summary>
/// Where a recipe's files land, and what a recipe is called when something has
/// to name it.
///
/// <para>Both rules used to live in <c>CookbookEndpoints</c>, which was fine
/// while the ZIP download was the only way a recipe left the app. Issue #626
/// gave it a second one - a commit into a GitHub repository - and the two must
/// agree: a consultant who downloads a recipe and a colleague who takes the
/// pull request have to get the same files in the same folders.</para>
/// </summary>
internal static class RecipePaths
{
    /// <summary>
    /// Builds the path a recipe file is written to from its admin-authored
    /// <paramref name="relativePath"/> and <paramref name="fileName"/>, in a
    /// form that cannot escape the folder it is being written into (zip-slip on
    /// a download, a path outside the repository on a commit). Both values come
    /// from the database and an Editor controls them, so they are not trusted:
    /// separators are normalised, and empty, <c>.</c> and <c>..</c> segments are
    /// dropped before each surviving segment is sanitised. The <c>/</c>
    /// separators between real segments survive, so the recipe's folder
    /// structure is kept. See #481.
    /// </summary>
    public static string SafeEntryPath(string? relativePath, string fileName)
    {
        var combined = string.IsNullOrEmpty(relativePath)
            ? fileName
            : relativePath + "/" + fileName;
        // Drop "." and ".." on the *raw* segment before sanitising - the
        // sanitiser keeps dots, so ".." would otherwise survive as a traversal
        // token.
        var segments = combined
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != "." && s != "..")
            .Select(SanitiseSegment)
            .ToList();
        // Everything collapsed away (e.g. a path made only of separators and
        // ".." segments) - emit a neutral name rather than a bare dot-segment.
        return segments.Count > 0 ? string.Join('/', segments) : "file";
    }

    /// <summary>
    /// A lower-case, hyphenated form of a recipe title, for the ZIP file name
    /// and for the branch a pull request is opened from. Empty when the title
    /// carries nothing that survives (a title made entirely of punctuation or
    /// non-ASCII letters), which each caller answers in its own way.
    /// </summary>
    public static string Slugify(string input)
    {
        var sb = new System.Text.StringBuilder(input.Length);
        var lastWasDash = true; // suppress leading dash
        foreach (var raw in input)
        {
            var c = char.ToLowerInvariant(raw);
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(c);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }
        return sb.ToString().TrimEnd('-');
    }

    /// <summary>
    /// One path segment, with everything that is not a letter, digit,
    /// <c>.</c>, <c>-</c> or <c>_</c> replaced by a hyphen. Same rule the
    /// download filenames use (<c>EndpointHelpers.SanitiseFileName</c>); it is
    /// repeated here rather than referenced so a service does not have to reach
    /// into the endpoint layer for it.
    /// </summary>
    private static string SanitiseSegment(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "file";
        var chars = new char[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            chars[i] = char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-';
        }
        return new string(chars);
    }
}
