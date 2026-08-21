using System.Text.Json;
using ALDevToolbox.Services.Diff;
using DiffPlex.DiffBuilder;
using FluentAssertions;

namespace ALDevToolbox.Tests.Diff;

/// <summary>
/// Covers the inline (unified) compare view's document — the one the viewer
/// mounts for #576. Side-by-side hands each pane a real file and aligns them
/// with blank fillers; unified has no file to hand over, so this class builds
/// the document, and everything that used to be true because the text WAS the
/// file has to be re-established here.
///
/// Three of those are easy to get subtly wrong and impossible to see in a
/// screenshot of a small diff.
///
/// <b>The line numbers are no longer the document's.</b> Row 12 of a unified
/// document is not line 12 of anything; it carries an old number, a new number,
/// or one of each. Get the pairing wrong and the gutters still render, still
/// ascend, and quietly point at the wrong code.
///
/// <b>A modified line is two rows, not one.</b> Old above new. Collapse it to
/// one and the reader cannot see what the line used to say, which is the only
/// reason to read a diff inline.
///
/// <b>The collapse has to keep an identical file whole.</b> Nothing changed
/// means nothing to hunk; a file collapsed to zero rows reads as a failure to
/// load, not as "these are the same".
/// </summary>
public sealed class UnifiedDiffSerializerTests
{
    [Fact]
    public void A_modified_line_becomes_two_rows_old_above_new()
    {
        var model = SideBySideDiffBuilder.Diff("a\nOLD\nc\n", "a\nNEW\nc\n");
        var unified = UnifiedDiffSerializer.Build(model);

        var lines = unified.Content.Split('\n');
        lines.Should().Equal("a", "OLD", "NEW", "c", "");

        Kinds(unified).Should().BeEquivalentTo(new[]
        {
            (line: 2, kind: "deleted"),
            (line: 3, kind: "modified"),
        }, because: "the old text is what went away and the new text is what replaced it");
    }

    [Fact]
    public void Each_row_carries_the_line_numbers_of_the_sides_it_exists_on()
    {
        var model = SideBySideDiffBuilder.Diff("a\nOLD\nc\n", "a\nNEW\nc\n");
        var unified = UnifiedDiffSerializer.Build(model);

        // "a" is line 1 on both. OLD is old-side only, NEW is new-side only,
        // and "c" is line 3 on both — the unified document's own row 4.
        Gutters(unified).Should().Equal(
            (1, 1),
            (2, null),
            (null, 2),
            (3, 3),
            (4, 4));
    }

    [Fact]
    public void An_insertion_has_no_old_number_and_a_deletion_has_no_new_one()
    {
        var model = SideBySideDiffBuilder.Diff("a\nb\n", "a\nINS\nb\n");
        var unified = UnifiedDiffSerializer.Build(model);

        Gutters(unified).Should().Equal((1, 1), (null, 2), (2, 3), (3, 4));
        Kinds(unified).Should().ContainSingle().Which.Should().Be((2, "inserted"));
    }

    [Fact]
    public void Word_ranges_land_on_the_unified_row_not_the_source_line()
    {
        // The change is on source line 4 of both sides, but on unified row 4
        // (old) and 5 (new) — a word range emitted against the source line
        // would paint two rows too high.
        var model = SideBySideDiffBuilder.Diff(
            "a\nb\nc\nvalue := 1;\ne\n",
            "a\nb\nc\nvalue := 2;\ne\n");
        var unified = UnifiedDiffSerializer.Build(model);

        var rows = JsonSerializer.Deserialize<List<WordRow>>(unified.WordDiff, Json)!;
        rows.Should().NotBeEmpty();
        rows.Select(r => r.Line).Should().OnlyContain(l => l == 4 || l == 5);
    }

    // ── The collapse ────────────────────────────────────────────────────

    [Fact]
    public void Unchanged_runs_beyond_the_context_window_are_dropped()
    {
        // 20 unchanged lines, one change in the middle. With three lines of
        // context either side, the reader should never scroll past the rest.
        var left = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line{i}")) + "\n";
        var right = left.Replace("line10", "CHANGED");

        var model = SideBySideDiffBuilder.Diff(left, right);
        var unified = UnifiedDiffSerializer.Build(model);
        var lines = unified.Content.Split('\n');

        // Three lines of context, the change (old row above new), three more.
        lines.Should().Equal("line7", "line8", "line9", "line10", "CHANGED", "line11", "line12", "line13");
        lines.Should().NotContain("line1");
        lines.Should().NotContain("line20");
    }

    [Fact]
    public void Two_distant_changes_become_two_hunks_with_a_banner_each()
    {
        var left = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"line{i}")) + "\n";
        var right = left.Replace("line5", "FIRST").Replace("line35", "SECOND");

        var model = SideBySideDiffBuilder.Diff(left, right);
        var unified = UnifiedDiffSerializer.Build(model);
        var hunks = Hunks(unified);

        hunks.Should().HaveCount(2, "the two changes are 30 lines apart, well beyond the context window");
        hunks[0].Before.Should().Be(1, "the first banner sits above the document's first row");
        hunks[1].Before.Should().BeGreaterThan(1);
        hunks.Should().OnlyContain(h => h.Header.StartsWith("@@ -") && h.Header.Contains(" +"));
    }

    [Fact]
    public void Adjacent_changes_stay_in_one_hunk()
    {
        var left = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line{i}")) + "\n";
        var right = left.Replace("line10", "A").Replace("line12", "B");

        var model = SideBySideDiffBuilder.Diff(left, right);
        var unified = UnifiedDiffSerializer.Build(model);

        Hunks(unified).Should().ContainSingle(
            because: "two lines apart is inside the context window, so there is no gap to banner");
    }

    [Fact]
    public void An_identical_pair_keeps_every_line_and_gets_no_banner()
    {
        var text = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line{i}")) + "\n";
        var unified = UnifiedDiffSerializer.Build(SideBySideDiffBuilder.Diff(text, text));

        unified.Content.Split('\n').Should().HaveCount(31, "nothing collapses when nothing changed");
        Hunks(unified).Should().BeEmpty();
        Kinds(unified).Should().BeEmpty();
    }

    // ── The banner ──────────────────────────────────────────────────────

    [Fact]
    public void The_banner_counts_the_rows_each_side_contributes()
    {
        var model = SideBySideDiffBuilder.Diff("a\nb\n", "a\nINS\nb\n");
        var header = Hunks(UnifiedDiffSerializer.Build(model)).Single().Header;

        // Three rows: "a" (both), "INS" (new only), "b" (both).
        // Four rows: "a" (both), "INS" (new only), "b" (both), the trailing
        // empty line (both) — three old-side, four new-side.
        header.Should().StartWith("@@ -1,3 +1,4 @@");
    }

    [Fact]
    public void The_banner_names_the_declaration_the_hunk_sits_in()
    {
        var left = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line{i}")) + "\n";
        var right = left.Replace("line10", "CHANGED");
        var model = SideBySideDiffBuilder.Diff(left, right);

        var unified = UnifiedDiffSerializer.Build(model, [
            new UnifiedDiffSerializer.Declaration(1, 5, "OpenPage"),
            new UnifiedDiffSerializer.Declaration(6, 20, "BlockCustomer"),
        ]);

        Hunks(unified).Single().Header.Should().EndWith(" @@ BlockCustomer",
            because: "the counts say how much changed; the name says where you are");
    }

    [Fact]
    public void The_banner_ends_at_the_counts_when_nothing_encloses_the_hunk()
    {
        var left = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line{i}")) + "\n";
        var model = SideBySideDiffBuilder.Diff(left, left.Replace("line10", "CHANGED"));

        Hunks(UnifiedDiffSerializer.Build(model, [])).Single().Header
            .Should().MatchRegex(@"^@@ -\d+,\d+ \+\d+,\d+ @@$");
    }

    [Fact]
    public void The_innermost_enclosing_declaration_wins()
    {
        var left = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line{i}")) + "\n";
        var model = SideBySideDiffBuilder.Diff(left, left.Replace("line10", "CHANGED"));

        var unified = UnifiedDiffSerializer.Build(model, [
            new UnifiedDiffSerializer.Declaration(1, 20, "TheWholeObject"),
            new UnifiedDiffSerializer.Declaration(8, 14, "TheProcedure"),
        ]);

        Hunks(unified).Single().Header.Should().EndWith("TheProcedure");
    }

    [Fact]
    public void The_old_sides_declarations_name_the_hunk_when_the_new_side_has_none()
    {
        // An outline is per-file, and a file that has not been through the
        // object pass has none — which is the common case on one side of a
        // release compare. The banner should still say where you are.
        var left = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line{i}")) + "\n";
        var model = SideBySideDiffBuilder.Diff(left, left.Replace("line10", "CHANGED"));

        var unified = UnifiedDiffSerializer.Build(
            model,
            declarations: [],
            oldDeclarations: [new UnifiedDiffSerializer.Declaration(6, 20, "BlockCustomer")]);

        Hunks(unified).Single().Header.Should().EndWith("BlockCustomer");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static List<(int Line, string Kind)> Kinds(UnifiedDiffSerializer.UnifiedDiff d) =>
        JsonSerializer.Deserialize<List<KindRow>>(d.Rows, Json)!.Select(r => (r.Line, r.Kind)).ToList();

    /// <summary>The gutter pairs as tuples — arrays compare by reference.</summary>
    private static List<(int? Old, int? New)> Gutters(UnifiedDiffSerializer.UnifiedDiff d) =>
        JsonSerializer.Deserialize<List<int?[]>>(d.Gutters, Json)!.Select(g => (g[0], g[1])).ToList();

    private static List<HunkRow> Hunks(UnifiedDiffSerializer.UnifiedDiff d) =>
        JsonSerializer.Deserialize<List<HunkRow>>(d.Hunks, Json)!;

    private sealed record KindRow(int Line, string Kind);
    private sealed record HunkRow(int Before, string Header);
    private sealed record WordRow(int Line, int From, int To);
}
