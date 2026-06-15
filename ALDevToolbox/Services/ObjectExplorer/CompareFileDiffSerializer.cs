using System.Linq;
using System.Text.Json;
using DiffPlex.DiffBuilder.Model;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Translates one side of a <see cref="SideBySideDiffModel"/> into the compact
/// <c>[{line, kind}, …]</c> JSON array that the source-viewer's CodeMirror
/// glue applies as line decorations.
///
/// The subtlety this class exists to encapsulate: DiffPlex pads each
/// <see cref="DiffPaneModel"/> with <see cref="ChangeType.Imaginary"/>
/// placeholder rows to visually align with the opposite pane. Those rows
/// have no counterpart in the source text the viewer renders, so using the
/// pane-index would drift every subsequent decoration off by the count of
/// preceding imaginaries. <see cref="DiffPiece.Position"/> is the actual
/// 1-based line in the source (null for Imaginary rows) — that's the value
/// CodeMirror needs.
/// </summary>
internal static class CompareFileDiffSerializer
{
    public static string SerializeSide(DiffPaneModel pane)
    {
        var rows = new List<object>();
        foreach (var line in pane.Lines)
        {
            if (line.Type is ChangeType.Unchanged or ChangeType.Imaginary) continue;
            if (line.Position is not int position) continue;
            rows.Add(new
            {
                line = position,
                kind = MapKind(line.Type),
            });
        }
        return JsonSerializer.Serialize(rows);
    }

    /// <summary>
    /// Emits the alignment gaps for one pane: the visual counterpart to the
    /// <see cref="ChangeType.Imaginary"/> rows that <see cref="SerializeSide"/>
    /// deliberately drops. DiffPlex pads each pane with imaginary placeholders
    /// where the opposite pane has extra lines; the CodeMirror viewer renders
    /// only real source, so to keep matching lines aligned (KDiff3-style) we
    /// turn each run of imaginaries into a blank filler block of <c>size</c>
    /// line-heights, anchored <c>before</c> the next real source line.
    ///
    /// <para>Output: <c>[{before, size}, …]</c>. <c>before</c> is the 1-based
    /// source line the gap precedes; a trailing run of imaginaries (the
    /// opposite pane appended lines past this pane's end) uses the sentinel
    /// <c>before = lineCount + 1</c> so the viewer attaches it after the last
    /// line. Empty <c>[]</c> when the panes are already the same length.</para>
    /// </summary>
    public static string SerializeFillers(DiffPaneModel pane)
    {
        var fillers = new List<object>();
        var realLineCount = 0;
        var pendingGap = 0;
        foreach (var line in pane.Lines)
        {
            if (line.Type is ChangeType.Imaginary)
            {
                pendingGap++;
                continue;
            }
            realLineCount++;
            if (pendingGap > 0 && line.Position is int position)
            {
                fillers.Add(new { before = position, size = pendingGap });
                pendingGap = 0;
            }
            else if (pendingGap > 0)
            {
                // Real row without a Position is unexpected, but don't lose the
                // gap: flush it before this line's (computed) source index.
                fillers.Add(new { before = realLineCount, size = pendingGap });
                pendingGap = 0;
            }
        }
        // A run of imaginaries at the end has no following real row to anchor
        // to — sentinel it past the last source line.
        if (pendingGap > 0)
        {
            fillers.Add(new { before = realLineCount + 1, size = pendingGap });
        }
        return JsonSerializer.Serialize(fillers);
    }

    /// <summary>
    /// Per-side change counts for the compare header. Modified lines appear
    /// (aligned) in both panes, so they're counted once from the new side;
    /// inserted/deleted are unique to their pane.
    /// </summary>
    public readonly record struct DiffSummary(int Added, int Removed, int Modified)
    {
        public int Total => Added + Removed + Modified;
        public bool Identical => Total == 0;
    }

    public static DiffSummary Summarize(SideBySideDiffModel model) => new(
        Added: model.NewText.Lines.Count(l => l.Type == ChangeType.Inserted),
        Removed: model.OldText.Lines.Count(l => l.Type == ChangeType.Deleted),
        Modified: model.NewText.Lines.Count(l => l.Type == ChangeType.Modified));

    public static string MapKind(ChangeType type) => type switch
    {
        ChangeType.Inserted => "inserted",
        ChangeType.Deleted => "deleted",
        ChangeType.Modified => "modified",
        ChangeType.Imaginary => "imaginary",
        _ => "unchanged",
    };
}
