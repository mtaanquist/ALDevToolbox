using System.Text;
using System.Text.Json;
using DiffPlex.DiffBuilder.Model;

namespace ALDevToolbox.Services.Diff;

/// <summary>
/// Builds the *unified* (inline) view of a <see cref="SideBySideDiffModel"/>:
/// one document interleaving both sides, the way the design handoff's Inline
/// tab renders it (<c>.design/handoff/PageCompare.dc.html</c>, see #576).
///
/// <para>Side-by-side needs no new document — each pane already holds a real
/// file, and the alignment is fillers on top. Unified has no such file: the
/// document is synthesised here, so the line numbers stop being the file's and
/// have to be carried alongside, one pair per row (<see cref="Gutters"/>). It
/// is also why hunks (#579) cost nothing on this path and a lot on the other:
/// we simply do not emit the runs of unchanged code, where side-by-side would
/// have to fold them and keep both panes in step through the fold.</para>
///
/// <para>The row kinds match the ones <see cref="SideBySideDiffSerializer"/>
/// already emits, so the viewer paints a unified document with the decoration
/// machinery it uses for a side pane — nothing new to tint.</para>
/// </summary>
public static class UnifiedDiffSerializer
{
    /// <summary>
    /// Unchanged lines kept either side of a change. Three is the long-standing
    /// diff default and it is what the handoff's sample hunks show; enough to
    /// recognise where you are, short enough that the collapse is the point.
    /// </summary>
    public const int ContextLines = 3;

    /// <summary>
    /// Everything the viewer needs to mount the inline pane. <see cref="Content"/>
    /// is the document text; the rest are JSON arrays addressed by 1-based line
    /// numbers *within that document*, never within either source file.
    /// </summary>
    /// <param name="Content">The unified document.</param>
    /// <param name="Rows">[{line, kind}] for every changed row, as per-side.</param>
    /// <param name="Gutters">[[old, new], …] one pair per document line; a
    /// null means the row does not exist on that side.</param>
    /// <param name="Collapse">[{index, header, from, to, before}] — the bands,
    /// in document line numbers. Same shape and same client code as the
    /// side-by-side panes use; see <see cref="CollapseRegion"/>.</param>
    /// <param name="WordDiff">[{line, from, to}] intra-line ranges, 1-based
    /// columns, <c>to</c> exclusive.</param>
    public readonly record struct UnifiedDiff(
        string Content,
        string Rows,
        string Gutters,
        string Collapse,
        string WordDiff);

    /// <summary>An enclosing declaration on the new side, for the <c>@@</c> banner's tail.</summary>
    /// <param name="StartLine">1-based first line of the declaration.</param>
    /// <param name="EndLine">1-based last line, or null when the outline didn't resolve one.</param>
    /// <param name="Name">What to print — the procedure or object name.</param>
    public readonly record struct Declaration(int StartLine, int? EndLine, string Name);

    /// <summary>
    /// Interleaves the two sides into one document and returns it with the
    /// per-row metadata.
    ///
    /// <para><paramref name="declarations"/> are new-side ranges used only to
    /// name each hunk. <paramref name="oldDeclarations"/> is the same for the
    /// old side and is consulted only when the new side has nothing to say —
    /// an outline is per-file and a file that has not been through the object
    /// pass has none, in which case the other side still knows which procedure
    /// this is. Pass neither and the banners end after the line counts, which
    /// is what plain <c>diff -u</c> does.</para>
    /// </summary>
    public static UnifiedDiff Build(
        SideBySideDiffModel model,
        IReadOnlyList<Declaration>? declarations = null,
        IReadOnlyList<Declaration>? oldDeclarations = null)
    {
        var rows = Interleave(model);

        // Every row goes into the document, including the unchanged runs the
        // reader will never scroll past. They are hidden by the bands rather
        // than left out, which is what makes them recoverable — see
        // <see cref="CollapseRegions"/>.
        var text = new StringBuilder();
        var kinds = new List<object>();
        var gutters = new List<int?[]>(rows.Count);
        var words = new List<object>();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var line = i + 1;
            if (i > 0) text.Append('\n');
            text.Append(row.Text);

            gutters.Add([row.OldLine, row.NewLine]);
            if (row.Kind is not "unchanged")
            {
                kinds.Add(new { line, kind = row.Kind });
            }
            foreach (var (from, to) in row.Words)
            {
                words.Add(new { line, from, to });
            }
        }

        return new UnifiedDiff(
            text.ToString(),
            JsonSerializer.Serialize(kinds),
            JsonSerializer.Serialize(gutters),
            CollapseJson.Serialize(CollapseRegions(rows, declarations, oldDeclarations)),
            JsonSerializer.Serialize(words));
    }

    /// <summary>
    /// One row of the unified document, before the unchanged runs are dropped.
    /// A modified line becomes two rows — the old text then the new — which is
    /// what makes an inline diff readable at all.
    /// </summary>
    private sealed record Row(string Text, string Kind, int? OldLine, int? NewLine, List<(int From, int To)> Words)
    {
        public bool IsChange => Kind is not "unchanged";
    }

    /// <summary>
    /// Walks the two panes in lockstep. DiffPlex pads both to the same length
    /// with <see cref="ChangeType.Imaginary"/> rows, so index i on one side is
    /// always the counterpart of index i on the other — that padding is exactly
    /// the alignment the side-by-side view renders as blank fillers, and here
    /// it tells us which side a row belongs to.
    /// </summary>
    private static List<Row> Interleave(SideBySideDiffModel model)
    {
        var old = model.OldText.Lines;
        var neu = model.NewText.Lines;
        var count = Math.Max(old.Count, neu.Count);
        var rows = new List<Row>(count + 16);

        for (var i = 0; i < count; i++)
        {
            var o = i < old.Count ? old[i] : null;
            var n = i < neu.Count ? neu[i] : null;

            // A changed line shows both texts, old above new — the one case
            // where a single aligned row becomes two document rows.
            if (o?.Type is ChangeType.Modified || n?.Type is ChangeType.Modified)
            {
                if (o is not null) rows.Add(RowFor(o, "deleted", o.Position, null));
                if (n is not null) rows.Add(RowFor(n, "modified", null, n.Position));
                continue;
            }

            if (o is not null && o.Type is ChangeType.Deleted)
            {
                rows.Add(RowFor(o, "deleted", o.Position, null));
            }
            if (n is not null && n.Type is ChangeType.Inserted)
            {
                rows.Add(RowFor(n, "inserted", null, n.Position));
            }
            if (o?.Type is ChangeType.Unchanged && n?.Type is ChangeType.Unchanged)
            {
                rows.Add(RowFor(n, "unchanged", o.Position, n.Position));
            }
        }
        return rows;
    }

    private static Row RowFor(DiffPiece piece, string kind, int? oldLine, int? newLine) =>
        new(piece.Text ?? string.Empty, kind, oldLine, newLine, WordRanges(piece));

    /// <summary>
    /// The changed word runs inside a modified line, as 1-based column ranges.
    /// Mirrors <see cref="SideBySideDiffSerializer.SerializeWordDiff"/> — only
    /// modified lines carry sub-pieces worth painting, since on an inserted or
    /// deleted line the whole row is the change.
    /// </summary>
    private static List<(int From, int To)> WordRanges(DiffPiece piece)
    {
        var ranges = new List<(int, int)>();
        if (piece.Type is not ChangeType.Modified) return ranges;
        if (piece.SubPieces is not { Count: > 0 } pieces) return ranges;

        var col = 1;
        foreach (var sub in pieces)
        {
            var length = sub.Text?.Length ?? 0;
            if (length == 0) continue;
            if (sub.Type is not ChangeType.Unchanged and not ChangeType.Imaginary)
            {
                ranges.Add((col, col + length));
            }
            col += length;
        }
        return ranges;
    }

    /// <summary>
    /// The bands: which stretches of the document are unchanged far enough
    /// from any change to be worth hiding, and what each band says.
    ///
    /// <para>Line numbers are the <b>document's</b> — row numbers in the
    /// interleaved text, matching neither side's file. That is fine and it is
    /// the whole trick: the client hides what it is told to hide, and does not
    /// need to know that this document was synthesised while a side-by-side
    /// pane holds a real file.</para>
    ///
    /// <para>This used to drop the runs outright, which is why the inline
    /// view's bands could not bring anything back — there was nothing behind
    /// them to reveal (#585). Switching layouts lost the context the reader had
    /// just expanded, with no way to get it back short of Side by side.</para>
    ///
    /// <para>An identical pair gets no bands at all: nothing changed, so there
    /// is no hunk to announce, and a <c>@@</c> header over a whole unchanged
    /// file says nothing true.</para>
    /// </summary>
    private static List<CollapseRegion> CollapseRegions(
        List<Row> rows,
        IReadOnlyList<Declaration>? declarations,
        IReadOnlyList<Declaration>? oldDeclarations)
    {
        var regions = new List<CollapseRegion>();
        if (!rows.Any(r => r.IsChange)) return regions;

        var keep = new bool[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            if (!rows[i].IsChange) continue;
            var from = Math.Max(0, i - ContextLines);
            var to = Math.Min(rows.Count - 1, i + ContextLines);
            for (var j = from; j <= to; j++) keep[j] = true;
        }

        var index = 0;
        var firstHunk = true;
        var at = 0;

        while (at < rows.Count)
        {
            if (keep[at])
            {
                // A hunk that opens the document has no hidden run to stand in
                // for it, but it still gets a banner — so emit a band that
                // hides nothing. Mirrors SideBySideCollapse; the two views
                // banner the same seams.
                if (firstHunk)
                {
                    regions.Add(new CollapseRegion(
                        index++, Banner(rows, keep, at, declarations, oldDeclarations),
                        From: null, To: null, Before: at + 1));
                    firstHunk = false;
                }
                at++;
                continue;
            }

            var hiddenFrom = at + 1;
            while (at < rows.Count && !keep[at]) at++;
            var hiddenTo = at;

            // The band names the hunk it introduces. Past the last hunk there
            // is none, so it says what it is hiding instead.
            var hidden = hiddenTo - hiddenFrom + 1;
            var header = at < rows.Count
                ? Banner(rows, keep, at, declarations, oldDeclarations)
                : $"... {hidden} unchanged {(hidden == 1 ? "line" : "lines")}";

            regions.Add(new CollapseRegion(index++, header, hiddenFrom, hiddenTo, Before: null));
            firstHunk = false;
        }

        return regions;
    }

    /// <summary>
    /// The <c>@@ -old,count +new,count @@ declaration</c> banner for the hunk
    /// starting at row <paramref name="start"/>. The counts run to the end of
    /// the hunk — the next hidden row. The tail names whatever declaration
    /// encloses the hunk's first CHANGED line, not its first line, which is
    /// three rows of context earlier and can easily sit outside the procedure
    /// the change is in. The counts say how much; the name says where, and it
    /// is the half of the banner a reader actually uses.
    /// </summary>
    private static string Banner(
        List<Row> rows,
        bool[] keep,
        int start,
        IReadOnlyList<Declaration>? declarations,
        IReadOnlyList<Declaration>? oldDeclarations)
    {
        int oldStart = 0, newStart = 0, oldCount = 0, newCount = 0;
        int? changedNew = null, changedOld = null;

        // Bounded by `keep`, so the hunk ends where the next hidden run
        // begins. The old form walked the trimmed row list looking for a
        // discontinuity in the line numbers, which had to guess at insertions
        // and deletions — those have a number on one side only.
        for (var i = start; i < rows.Count && keep[i]; i++)
        {
            var row = rows[i];
            if (row.OldLine is int o)
            {
                if (oldStart == 0) oldStart = o;
                oldCount++;
                if (row.IsChange) changedOld ??= o;
            }
            if (row.NewLine is int n)
            {
                if (newStart == 0) newStart = n;
                newCount++;
                if (row.IsChange) changedNew ??= n;
            }
        }

        var banner = $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@";
        var enclosing = Enclosing(declarations, changedNew) ?? Enclosing(oldDeclarations, changedOld);
        return enclosing is null ? banner : $"{banner} {enclosing}";
    }

    private static string? Enclosing(IReadOnlyList<Declaration>? declarations, int? line)
    {
        if (declarations is null || line is not int at) return null;
        string? best = null;
        var bestStart = 0;
        foreach (var d in declarations)
        {
            if (d.StartLine > at) continue;
            if (d.EndLine is int end && end < at) continue;
            // Innermost wins: declarations can nest, and the nearest one above
            // is the one that names where you are.
            if (d.StartLine >= bestStart)
            {
                bestStart = d.StartLine;
                best = d.Name;
            }
        }
        return best;
    }
}
