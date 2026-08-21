using System.Text.Json;
using DiffPlex.DiffBuilder.Model;

namespace ALDevToolbox.Services.Diff;

/// <summary>
/// Collapses the unchanged stretches of a side-by-side diff, the way the design
/// handoff's compare screen renders it (<c>.design/handoff/PageCompare.dc.html</c>,
/// see #579): a run of <c>@@</c> banners with only the changed regions and their
/// context between them.
///
/// <para>The inline view gets this for free — it synthesises its document, so
/// the unchanged runs are simply never emitted (<see cref="UnifiedDiffSerializer"/>).
/// Side-by-side cannot: each pane holds a real file, and the two are kept in
/// step by blank filler rows measured against the full text. Hide lines in one
/// pane and the other has to hide exactly as many, or every line below the gap
/// stops facing its counterpart.</para>
///
/// <para>What makes that tractable is that a collapsed run is <i>unchanged on
/// both sides</i> by construction, so its rows pair one-to-one and hold no
/// fillers. Hiding aligned rows a..b therefore removes the same height from
/// both panes, whatever the line numbers on either side happen to be. Every
/// region below carries a shared <see cref="Region.Index"/> so the two panes
/// can be expanded together.</para>
/// </summary>
public static class SideBySideCollapse
{
    /// <summary>Unchanged lines kept either side of a change. Same window the inline view uses.</summary>
    public const int ContextLines = UnifiedDiffSerializer.ContextLines;

    /// <summary>
    /// One band in a collapsed pane. Either it stands in for hidden lines
    /// (<see cref="From"/>..<see cref="To"/>, and clicking it brings them back),
    /// or it hides nothing and simply announces the hunk below it — which is
    /// what the banner above a diff that starts at line 1 does.
    /// </summary>
    /// <param name="Index">Shared across both panes: expanding is a pair operation.</param>
    /// <param name="Header">The band's text.</param>
    /// <param name="From">First hidden line on this side, or null when nothing is hidden.</param>
    /// <param name="To">Last hidden line on this side.</param>
    /// <param name="Before">Line the band sits above, when it hides nothing.</param>
    public readonly record struct Region(int Index, string Header, int? From, int? To, int? Before);

    /// <summary>
    /// Builds both panes' regions from one walk. Returns them already paired:
    /// <c>Old</c> for the left pane, <c>New</c> for the right, same indices in
    /// the same order.
    /// </summary>
    public static (string Old, string New) Serialize(
        SideBySideDiffModel model,
        IReadOnlyList<UnifiedDiffSerializer.Declaration>? declarations = null,
        IReadOnlyList<UnifiedDiffSerializer.Declaration>? oldDeclarations = null)
    {
        var (old, neu) = Build(model, declarations, oldDeclarations);
        return (JsonSerializer.Serialize(old, JsonOptions), JsonSerializer.Serialize(neu, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The regions for each pane. Exposed for tests; the page uses <see cref="Serialize"/>.</summary>
    public static (List<Region> Old, List<Region> New) Build(
        SideBySideDiffModel model,
        IReadOnlyList<UnifiedDiffSerializer.Declaration>? declarations = null,
        IReadOnlyList<UnifiedDiffSerializer.Declaration>? oldDeclarations = null)
    {
        var oldLines = model.OldText.Lines;
        var newLines = model.NewText.Lines;
        var rows = Math.Max(oldLines.Count, newLines.Count);
        var oldRegions = new List<Region>();
        var newRegions = new List<Region>();
        if (rows == 0) return (oldRegions, newRegions);

        // An aligned row is interesting when either side reports anything but
        // an unchanged line. Imaginary counts: it is the placeholder facing the
        // other side's insertion, so it is where a change is.
        var interesting = new bool[rows];
        for (var i = 0; i < rows; i++)
        {
            var o = i < oldLines.Count ? oldLines[i].Type : ChangeType.Imaginary;
            var n = i < newLines.Count ? newLines[i].Type : ChangeType.Imaginary;
            interesting[i] = o is not ChangeType.Unchanged || n is not ChangeType.Unchanged;
        }
        if (!interesting.Any(x => x)) return (oldRegions, newRegions);

        var keep = new bool[rows];
        for (var i = 0; i < rows; i++)
        {
            if (!interesting[i]) continue;
            for (var j = Math.Max(0, i - ContextLines); j <= Math.Min(rows - 1, i + ContextLines); j++)
            {
                keep[j] = true;
            }
        }

        // Real lines consumed on each side as we walk. A pane's line number at
        // an aligned row is "how many real lines came before it, plus one" —
        // which is also where a band anchors when it hides nothing.
        var oldSeen = 0;
        var newSeen = 0;
        var index = 0;
        var i2 = 0;
        var firstHunk = true;

        while (i2 < rows)
        {
            if (keep[i2])
            {
                // A hunk that opens the file has no hidden run to stand in for
                // it, but the handoff still banners it — so emit a band that
                // hides nothing, anchored above the first line of each pane.
                if (firstHunk)
                {
                    var header = Banner(model, keep, i2, declarations, oldDeclarations);
                    oldRegions.Add(new Region(index, header, null, null, oldSeen + 1));
                    newRegions.Add(new Region(index, header, null, null, newSeen + 1));
                    index++;
                    firstHunk = false;
                }
                if (i2 < oldLines.Count && oldLines[i2].Type is not ChangeType.Imaginary) oldSeen++;
                if (i2 < newLines.Count && newLines[i2].Type is not ChangeType.Imaginary) newSeen++;
                i2++;
                continue;
            }

            // A dropped run. Every row in it is unchanged on both sides, so it
            // maps to a real line range on each — and to the same number of
            // rows, which is what keeps the panes level.
            var oldFrom = oldSeen + 1;
            var newFrom = newSeen + 1;
            while (i2 < rows && !keep[i2])
            {
                if (i2 < oldLines.Count && oldLines[i2].Type is not ChangeType.Imaginary) oldSeen++;
                if (i2 < newLines.Count && newLines[i2].Type is not ChangeType.Imaginary) newSeen++;
                i2++;
            }

            // The band names the hunk it introduces. Past the last hunk there
            // is none, so it says what it is hiding instead — the handoff's
            // sample file happens to end on its last change and never had to
            // answer this.
            var hidden = Math.Max(oldSeen - oldFrom + 1, newSeen - newFrom + 1);
            var text = i2 < rows
                ? Banner(model, keep, i2, declarations, oldDeclarations)
                : $"... {hidden} unchanged {(hidden == 1 ? "line" : "lines")}";

            oldRegions.Add(new Region(index, text, oldFrom, oldSeen, null));
            newRegions.Add(new Region(index, text, newFrom, newSeen, null));
            index++;
            firstHunk = false;
        }

        return (oldRegions, newRegions);
    }

    /// <summary>
    /// The <c>@@ -old,count +new,count @@ declaration</c> line for the hunk
    /// beginning at aligned row <paramref name="start"/>. Counts run to the end
    /// of the hunk — the next dropped row — and the tail names the declaration
    /// around the hunk's first CHANGED line, which is where the reader is
    /// going, not its first line of context.
    /// </summary>
    private static string Banner(
        SideBySideDiffModel model,
        bool[] keep,
        int start,
        IReadOnlyList<UnifiedDiffSerializer.Declaration>? declarations,
        IReadOnlyList<UnifiedDiffSerializer.Declaration>? oldDeclarations)
    {
        var oldLines = model.OldText.Lines;
        var newLines = model.NewText.Lines;
        int oldStart = 0, newStart = 0, oldCount = 0, newCount = 0;
        int? changedOld = null, changedNew = null;

        for (var i = start; i < keep.Length && keep[i]; i++)
        {
            var o = i < oldLines.Count ? oldLines[i] : null;
            var n = i < newLines.Count ? newLines[i] : null;
            var changed = o?.Type is not ChangeType.Unchanged || n?.Type is not ChangeType.Unchanged;

            if (o?.Position is int op)
            {
                if (oldStart == 0) oldStart = op;
                oldCount++;
                if (changed) changedOld ??= op;
            }
            if (n?.Position is int np)
            {
                if (newStart == 0) newStart = np;
                newCount++;
                if (changed) changedNew ??= np;
            }
        }

        var banner = $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@";
        var enclosing = Enclosing(declarations, changedNew) ?? Enclosing(oldDeclarations, changedOld);
        return enclosing is null ? banner : $"{banner} {enclosing}";
    }

    private static string? Enclosing(IReadOnlyList<UnifiedDiffSerializer.Declaration>? declarations, int? line)
    {
        if (declarations is null || line is not int at) return null;
        string? best = null;
        var bestStart = 0;
        foreach (var d in declarations)
        {
            if (d.StartLine > at) continue;
            if (d.EndLine is int end && end < at) continue;
            if (d.StartLine >= bestStart)
            {
                bestStart = d.StartLine;
                best = d.Name;
            }
        }
        return best;
    }
}
