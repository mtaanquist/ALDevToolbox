namespace ALDevToolbox.Services;

/// <summary>
/// Parses a tab-separated clipboard block (a range copied from Excel or a
/// spreadsheet) into a grid of trimmed cells. Used by the row-table admin
/// editors (catalogue, application versions) to map an Excel paste onto rows
/// and columns. Kept as a pure static helper so the parsing rules — the
/// newline/tab split, the trailing-newline trim spreadsheets append, and
/// <c>\r\n</c> normalisation — are unit-testable without a browser.
/// </summary>
public static class GridPasteParser
{
    /// <summary>
    /// Splits <paramref name="raw"/> into rows (on newlines) and cells (on
    /// tabs). Normalises <c>\r\n</c>/<c>\r</c> to <c>\n</c>, drops a single
    /// trailing newline that spreadsheets append to a copied range, and trims
    /// surrounding whitespace from every cell. Returns an empty grid for null
    /// or empty input.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return Array.Empty<IReadOnlyList<string>>();
        }

        var normalized = raw.Replace("\r\n", "\n").Replace('\r', '\n');
        // A copied spreadsheet range ends with one trailing newline; drop just
        // that one so it doesn't manifest as a spurious empty final row.
        if (normalized.EndsWith('\n'))
        {
            normalized = normalized[..^1];
        }

        if (normalized.Length == 0)
        {
            return Array.Empty<IReadOnlyList<string>>();
        }

        var lines = normalized.Split('\n');
        var rows = new List<IReadOnlyList<string>>(lines.Length);
        foreach (var line in lines)
        {
            var cells = line.Split('\t');
            for (var i = 0; i < cells.Length; i++)
            {
                cells[i] = cells[i].Trim();
            }
            rows.Add(cells);
        }
        return rows;
    }
}
