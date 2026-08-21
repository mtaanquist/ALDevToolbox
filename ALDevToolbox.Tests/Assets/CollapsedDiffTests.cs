using FluentAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards collapsing the unchanged stretches of the SIDE-BY-SIDE compare
/// (#579's harder half). <see cref="ALDevToolbox.Tests.Diff.SideBySideCollapseTests"/>
/// covers what the server computes; this covers the wiring, where the failures
/// are silent rather than wrong-looking.
///
/// <b>Hiding lines is a pair operation.</b> The panes are kept level by blank
/// filler rows measured against the full text, so a band expanded in one pane
/// and not the other puts every line below it opposite the wrong counterpart —
/// no error, no visual break at the seam, just a diff that stops meaning
/// anything half way down. That is why the bands carry a shared index instead
/// of a line range, and why the toggle drives both editors.
///
/// <b>A block widget with no height estimate counts as zero.</b> The same trap
/// the alignment fillers fell into (PR 16a): CodeMirror places off-screen rows
/// from the estimate, so a band that estimates nothing makes the pane grow as
/// the reader scrolls — and two panes growing at different moments are not
/// level even when they hide the same rows.
///
/// <b>Two block widgets at one position need an order.</b> A hunk banner and an
/// alignment filler can both anchor above the same line. The banner introduces
/// the gap, so it has to sort above it; equal `side` values leave the order to
/// whichever decoration source CodeMirror merged first.
/// </summary>
public sealed class CollapsedDiffTests
{
    private const string Page = "ALDevToolbox/Components/Pages/ObjectExplorer/OeCompareFile.razor";
    private const string ViewerJs = "ALDevToolbox/wwwroot/source-viewer.js";
    private const string EditorJs = "ALDevToolbox/wwwroot/code-editor.js";

    [Fact]
    public void Both_panes_are_serialised_from_one_paired_call()
    {
        var page = Read(Page);

        page.Should().Contain("(_leftCollapseJson, _rightCollapseJson) =",
            because: "two independent calls could drift into hiding different rows");
        page.Should().Contain("SideBySideCollapse.Serialize(model,");
        page.Should().Contain(@"data-collapse=""@_leftCollapseJson""");
        page.Should().Contain(@"data-collapse=""@_rightCollapseJson""");
    }

    [Fact]
    public void The_toggle_drives_both_panes()
    {
        // Live code only: a commented-out call reads the same to a substring
        // search, and "the right pane stopped expanding" is precisely the
        // change this test exists to catch.
        var handler = Code(Between(Read(ViewerJs), "function wireCollapseToggle()", "\n}\n"));

        handler.Should().Contain("toggleCollapsedRegion(panes.left.editorId, index)");
        handler.Should().Contain("toggleCollapsedRegion(panes.right.editorId, index)",
            because: "the panes are level only while they hide the same rows");
        handler.Should().Contain("aldt-toggle-region",
            because: "the bands are CodeMirror widgets and get rebuilt on every toggle, so the "
                     + "listener cannot live on the band");

        Read(EditorJs).Should().Contain("export function toggleCollapsedRegion(id, index)");
    }

    /// <summary>Drops comment-only lines, so a disabled call cannot satisfy a guard.</summary>
    private static string Code(string js) =>
        string.Join('\n', js.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        from.Should().BeGreaterThan(-1, $"'{start}' should exist");
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        to.Should().BeGreaterThan(from, $"'{start}' should be a complete function");
        return text[from..to];
    }

    [Fact]
    public void The_collapse_payload_reaches_the_mount()
    {
        var js = Read(ViewerJs);
        js.Should().Contain("codeHost.dataset.collapse");
        js.Should().Contain("collapse: Array.isArray(collapseData)");

        Read(EditorJs).Should().Contain("buildCollapseExtensions(opts.collapse)");
    }

    [Fact]
    public void A_hidden_stretch_is_replaced_as_a_block_not_folded_away()
    {
        var js = Read(EditorJs);

        js.Should().Contain("Decoration.replace({", "whole lines have to leave the layout");
        js.Should().Contain("widget: new CollapseBandWidget(text, r.index, true)",
            because: "something has to stand where the lines were, or the seam is invisible");
    }

    [Fact]
    public void Every_band_estimates_its_own_height()
    {
        var js = Read(EditorJs);

        // Both band widgets, and the filler that taught us this.
        js.Split("get estimatedHeight()").Length.Should().BeGreaterThanOrEqualTo(4,
            because: "FillerWidget, HunkWidget and CollapseBandWidget each place off-screen rows");
        js.Should().Contain("const HUNK_HEIGHT = 24;");
    }

    [Fact]
    public void A_banner_sorts_above_a_filler_at_the_same_line()
    {
        var js = Read(EditorJs);

        js.Should().NotContain("block: true,\n                side: -1,",
            because: "a banner level with a filler can render inside the gap it introduces");
        js.Should().Contain("side: -2");
    }

    /// <summary>
    /// A band that hides something is a control; one that only announces the
    /// hunk below it is not. The difference has to be visible on hover and
    /// reachable from the keyboard, since it is the only way back to the
    /// unchanged code without leaving the page.
    /// </summary>
    [Fact]
    public void A_band_that_hides_something_is_operable()
    {
        var js = Read(EditorJs);

        js.Should().Contain("if (index === null) return el;",
            because: "a band with nothing behind it must not look clickable");
        js.Should().Contain(@"el.setAttribute(""role"", ""button"")");
        js.Should().Contain("el.tabIndex = 0;");
        js.Should().Contain(@"e.key === ""Enter""");

        Read("ALDevToolbox/wwwroot/tools.css").Should()
            .Contain(@".cm-editor .hunk[role=""button""] { cursor: pointer; }");
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
