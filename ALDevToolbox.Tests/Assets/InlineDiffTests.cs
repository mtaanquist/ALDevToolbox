using AwesomeAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the inline (unified) compare layout — #576's tabs and #579's hunk
/// banners, which arrive together because both change what the pane renders
/// rather than how it looks.
///
/// Since #585 the inline pane collapses off the same payload the side-by-side
/// panes use (<c>data-collapse</c>), rather than a banners-only one. That is
/// what makes its bands able to bring the unchanged code back; before it, the
/// runs were never in the document to reveal.
///
/// The layout is three pieces that only work as a set, and two of the seams
/// fail quietly.
///
/// <b>The pane cannot mount at page load.</b> CodeMirror measures its own
/// rows, and inside a <c>hidden</c> container every measurement is zero — a
/// pane mounted early renders with no height and never recovers. So the
/// initial sweep skips it and the toggle mounts it on first reveal. Skipping
/// it there is load-bearing for a second reason: the side-by-side wiring keys
/// off there being exactly TWO compare roots, so a third one that joined the
/// sweep would stop the two panes ever being paired.
///
/// <b>The data attributes are a contract between two files.</b> The page
/// writes <c>data-unified-gutters</c> and <c>data-collapse</c>; source-viewer.js
/// reads them by those exact names and passes them on. A rename on either side
/// leaves a pane that mounts, renders code, and silently shows CodeMirror's own
/// row numbers for a document whose rows are not lines of any file.
///
/// <b>The design layer already had the CSS.</b> <c>.hunk</c> and
/// <c>.cmp__panes--inline</c> shipped with the handoff and nothing used them,
/// which is what #576 and #579 were filed about. If a class here stops matching
/// the sheet, the banner renders as unstyled text in the middle of the diff.
/// </summary>
public sealed class InlineDiffTests
{
    private const string Page = "ALDevToolbox/Components/Pages/ObjectExplorer/OeCompareFile.razor";
    private const string ViewerJs = "ALDevToolbox/wwwroot/source-viewer.js";
    private const string EditorJs = "ALDevToolbox/wwwroot/code-editor.js";
    private const string PagesPower = "ALDevToolbox/wwwroot/pages-power.css";

    [Fact]
    public void The_page_offers_both_layouts_and_ships_both_documents()
    {
        var page = Read(Page);

        page.Should().Contain("data-diff-layout", "the toggle is found by attribute, not by class");
        page.Should().Contain(@"data-layout=""side""");
        page.Should().Contain(@"data-layout=""inline""");
        page.Should().Contain(@"data-layout-pane=""side""");
        page.Should().Contain(@"data-layout-pane=""inline""");
        page.Should().Contain("cmp__panes--inline", "the design layer's one-column grid");

        // Shipped with the page, not fetched on switch — the whole reason the
        // toggle can be a class change.
        page.Should().Contain("data-unified-gutters=\"@_unifiedGuttersJson\"");
        page.Should().Contain("data-collapse=\"@_unifiedCollapseJson\"");
    }

    [Fact]
    public void The_inline_pane_starts_hidden_and_is_skipped_by_the_initial_sweep()
    {
        Read(Page).Should().Contain(@"data-layout-pane=""inline"" hidden",
            because: "a pane revealed by default would be measured before the reader chose it");

        var js = Read(ViewerJs);
        js.Should().Contain(@"root.classList.contains(""source-viewer--inline"")",
            because: "the sweep has to skip it — both so it mounts measured, and so the "
                     + "side-by-side pair stays a pair");
        js.Should().Contain("function mountInlinePane(", "the toggle owns the mount instead");
    }

    [Fact]
    public void The_unified_attribute_names_match_on_both_sides()
    {
        var js = Read(ViewerJs);

        // dataset camel-cases the attribute; these are the two spellings of
        // the same contract and they have to move together.
        js.Should().Contain("codeHost.dataset.unifiedGutters");
        js.Should().Contain("codeHost.dataset.collapse");
        js.Should().Contain("unifiedGutters:", "…and be handed on to the mount");
        js.Should().Contain("collapse: Array.isArray(collapseData)");

        var editor = Read(EditorJs);
        editor.Should().Contain("opts.unifiedGutters", "which is where the mount reads them");
        editor.Should().Contain("buildCollapseExtensions(opts.collapse)");
    }

    [Fact]
    public void The_layout_choice_is_remembered()
    {
        var js = Read(ViewerJs);
        js.Should().Contain("aldt-compare-layout", "a per-page reset makes the toggle an annoyance");
        js.Should().Contain("localStorage.setItem(LAYOUT_KEY");
        js.Should().Contain("localStorage.getItem(LAYOUT_KEY");
    }

    /// <summary>
    /// Next/previous has to move whatever is on screen. Before the inline
    /// layout there was only one answer, so the buttons called straight into
    /// the pane-pair walk; now that call has to route.
    /// </summary>
    [Fact]
    public void Change_navigation_follows_the_visible_layout()
    {
        var js = Read(ViewerJs);
        js.Should().Contain("changeNavMode", "one entry point, two walks");
        js.Should().Contain("function goInline(");
        js.Should().Contain("function goSideBySide(");
        js.Should().Contain(@"scrollToLine(inlinePane.editorId, target, true, ""top"")",
            because: "centring clamps to zero on a diff shorter than a viewport, which is "
                     + "exactly what a collapsed diff usually is");
    }

    [Fact]
    public void The_classes_the_banners_use_exist_in_the_design_layer()
    {
        var sheet = Read(PagesPower);
        sheet.Should().Contain(".hunk {", "the banner's own styling is the handoff's");
        sheet.Should().Contain(".cmp__panes--inline {");

        Read(EditorJs).Should().Contain(@"el.className = ""hunk""",
            because: "the widget has to claim the class the sheet paints");
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
