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

    public static string MapKind(ChangeType type) => type switch
    {
        ChangeType.Inserted => "inserted",
        ChangeType.Deleted => "deleted",
        ChangeType.Modified => "modified",
        ChangeType.Imaginary => "imaginary",
        _ => "unchanged",
    };
}
