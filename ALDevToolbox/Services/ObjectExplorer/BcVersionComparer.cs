namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Compares Business Central version strings (e.g. "28.1", "28.10",
/// "27.4.0.12345") numerically rather than lexicographically. Without this,
/// "28.10" sorts before "28.2" — which is the bug the Object Explorer
/// browser hit before this was wired in.
///
/// Missing parts are treated as zero so "28" and "28.0.0" compare equal.
/// Non-numeric segments are pushed to the bottom by treating their numeric
/// value as -1, which keeps the sort total without throwing on the
/// occasional non-conforming label.
/// </summary>
internal sealed class BcVersionComparer : IComparer<string?>
{
    public static readonly BcVersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        // null sorts after non-null so "no version" rows fall to the bottom
        // of a descending sort and to the top of an ascending one.
        if (x is null && y is null) return 0;
        if (x is null) return 1;
        if (y is null) return -1;

        var xs = x.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var ys = y.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var len = Math.Max(xs.Length, ys.Length);
        for (var i = 0; i < len; i++)
        {
            var xi = i < xs.Length && int.TryParse(xs[i], out var xv) ? xv : -1;
            var yi = i < ys.Length && int.TryParse(ys[i], out var yv) ? yv : -1;
            if (xi != yi) return xi.CompareTo(yi);
        }
        return 0;
    }
}
