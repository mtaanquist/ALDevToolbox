using System.Text.RegularExpressions;
using FluentAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards how the compare panes answer one question: how far down a pane does
/// source line N render?
///
/// It used to be arithmetic over the server's filler list — "the real lines
/// above it, plus the blank filler rows above it" — written out at four call
/// sites across two files. Four copies of a formula is four places to be
/// wrong, and the formula itself only holds while every row is present: fold
/// an unchanged region away and the rows above a line stop predicting where it
/// sits. The answer now comes from CodeMirror, which already tracks the lines,
/// the filler widgets and the folds: <c>lineTop</c> / <c>lineAtTop</c> /
/// <c>paneMetrics</c> in code-editor.js, used by everything that positions
/// against a pane.
///
/// Three things here fail silently, which is why they are pinned in a test
/// rather than left to review.
///
/// <b>A returning copy of the arithmetic looks right.</b> It agrees with the
/// view on every file that has no gaps, and most sample files have few — so a
/// reintroduced <c>(line - 1) + fillersAbove(line)</c> passes a casual look and
/// then slides the panes apart on the first file with real insertions.
///
/// <b>defaultLineHeight is a placeholder until the view measures.</b> CodeMirror
/// reports 14px — its own default, not our 19.6px rows — until the height
/// oracle has run, and construction time is before that. Read it synchronously
/// at mount and every alignment gap renders a third short: the panes ended up
/// 40px apart over four gaps, which reads as "the diff is slightly wrong"
/// rather than as a bug. Every read has to go through
/// <c>withMeasuredLineHeight</c>.
///
/// <b>The rendered gap and the estimated gap have to be the same number.</b>
/// <c>estimatedHeight</c> places every off-screen row; <c>toDOM</c> paints the
/// on-screen one. If toDOM re-reads the line height instead of using the value
/// the widget was built with, the height map and the DOM disagree about where
/// everything below the gap is, and the disagreement only shows once you
/// scroll.
/// </summary>
public sealed class CompareGeometryTests
{
    private const string EditorJs = "ALDevToolbox/wwwroot/code-editor.js";
    private const string ViewerJs = "ALDevToolbox/wwwroot/source-viewer.js";

    /// <summary>
    /// The retired formula, in any of the spellings the four call sites used.
    /// Each summed filler sizes for gaps anchored at or above a line and added
    /// that to the line's zero-based index.
    /// </summary>
    [Theory]
    [InlineData(EditorJs)]
    [InlineData(ViewerJs)]
    public void The_filler_row_arithmetic_is_gone(string file)
    {
        var js = Read(file);

        foreach (var name in new[] { "fillersAbove", "alignedRow", "lineAtAlignedRow", "visualOf", "offsetBefore", "totalVisual" })
        {
            js.Should().NotContain(name,
                because: "pane positions come from lineTop/lineAtTop, which stay right through a fold");
        }

        Regex.IsMatch(js, @"\(\s*line\s*-\s*1\s*\)\s*\+")
            .Should().BeFalse("'(line - 1) + fillers above' is the formula the view now answers");

        Regex.Matches(js, @"f\.before\s*<=").Should().BeEmpty(
            because: "summing filler sizes by anchor line is the arithmetic itself, whatever it is called");
    }

    [Fact]
    public void The_geometry_helpers_are_exported_and_used()
    {
        var editor = Read(EditorJs);
        foreach (var fn in new[] { "lineTop", "lineAtTop", "paneMetrics", "afterLayout" })
        {
            editor.Should().Contain($"export function {fn}(",
                because: "source-viewer.js imports it by name; a rename there is a silent undefined");
        }

        var viewer = Read(ViewerJs);
        viewer.Should().Contain("lineTop, lineAtTop, paneMetrics, afterLayout",
            because: "the import list is the only place the two modules agree on these names");
        viewer.Should().NotContain("__compareFillers",
            because: "the panes carry their own geometry now; a stashed filler list would go stale");
    }

    /// <summary>
    /// Both mounts set up fillers before CodeMirror has measured a row, so both
    /// have to defer. The check is that no filler / diff dispatch reads
    /// defaultLineHeight inline — <c>setDiff</c> may, because by then the pane
    /// has been on screen for a while.
    /// </summary>
    [Fact]
    public void Mount_time_line_heights_go_through_the_measured_read()
    {
        var js = Read(EditorJs);

        js.Should().Contain("function withMeasuredLineHeight(view, fn)");
        js.Should().NotContain("buildFillerDecorationExtensions(opts.fillers, view.defaultLineHeight)",
            because: "at construction time defaultLineHeight is CodeMirror's 14px placeholder");
        js.Should().NotContain("lineHeight: view.defaultLineHeight,",
            because: "same placeholder, reached through the diff effect instead of the compartment");

        // The one legitimate synchronous read: a live re-diff on a pane that
        // has already been measured.
        js.Should().Contain("lineHeight: rec.view.defaultLineHeight,");
    }

    [Fact]
    public void A_filler_renders_at_the_height_it_estimated()
    {
        var js = Read(EditorJs);

        js.Should().Contain("(this._lineHeight ?? view.defaultLineHeight) * this._size",
            because: "toDOM must paint the height estimatedHeight promised, or the height map lies");
        js.Should().Contain("return this._size * (this._lineHeight ?? FILLER_LINE_HEIGHT_FALLBACK);",
            because: "the estimate is the same number from the same field");
    }

    /// <summary>
    /// The follower pane is scrolled to a line's top plus the offset into it.
    /// That correction has to land outside CodeMirror's measure cycle: written
    /// from inside, the scrollIntoView it is correcting overwrites it, and the
    /// follower snaps to a row boundary up to a full row from where the user is.
    /// </summary>
    [Fact]
    public void The_scroll_sync_correction_runs_outside_the_measure_cycle()
    {
        var sync = Between(Read(EditorJs), "export function syncComparePanes", "\n}\n");

        sync.Should().Contain("requestAnimationFrame(", because: "the correction waits a frame for scrollIntoView to apply");
        sync.Should().NotContain("requestMeasure(", because: "a write from the read phase is what the scroll overwrote");
        sync.Should().Contain("b.top + frac", because: "the sub-line offset is the whole point of correcting");
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        from.Should().BeGreaterThan(-1, $"'{start}' should exist");
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        to.Should().BeGreaterThan(from, $"'{start}' should be a complete function");
        return text[from..to];
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(Root(), relative));

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ALDevToolbox.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
