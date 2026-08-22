using FluentAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the inline layout on the STANDALONE Compare tool (#581), which is a
/// different problem from the same layout on the Object Explorer's file diff.
/// There, the unified document is baked into the page and never moves. Here the
/// two panes ARE the input, so the document is a function of text the reader is
/// still typing — and both of the ways that can go wrong are silent.
///
/// <b>A stale pane looks current.</b> The inline document is rebuilt from the
/// server on every re-diff; a pane still showing the one from two keystrokes
/// ago renders perfectly and says something false. It is worse than a pane that
/// has not opened yet, which at least announces itself.
///
/// <b>The pane is a result, not a third input.</b> It is one read-only view
/// over a synthesised document, so switching to it while it took keystrokes
/// would take the text-entry surface away mid-task. It must not be editable,
/// and the reader must be told where editing lives rather than discovering it
/// by typing into a pane that will not answer.
/// </summary>
public sealed class CompareToolInlineTests
{
    private const string Page = "ALDevToolbox/Components/Pages/Compare.razor";
    private const string ViewerJs = "ALDevToolbox/wwwroot/source-viewer.js";

    /// <summary>
    /// The two side panes carry <c>data-editable="true"</c> and the inline one
    /// must not. initOne branches on exactly that attribute, so adding it here
    /// would mount a third editable compare pane — and the side-by-side pairing
    /// counts compare roots, which is the second thing it would break.
    /// </summary>
    [Fact]
    public void The_inline_pane_is_a_result_view_and_takes_no_input()
    {
        var page = Read(Page);
        var inline = Between(page, @"data-layout-pane=""inline""", "</div>\n            </div>");

        inline.Should().Contain("source-viewer--inline");
        inline.Should().NotContain("data-editable",
            because: "the panes that take input are the side-by-side pair; this one renders the answer");
        inline.Should().NotContain("data-placeholder",
            because: "a placeholder invites typing");
    }

    /// <summary>
    /// Saying "read-only" is not enough on its own — the reader needs to know
    /// where editing went, or the pane is a dead end they have to guess their
    /// way out of.
    /// </summary>
    [Fact]
    public void The_inline_pane_says_where_editing_lives()
    {
        Read(Page).Should().Contain("switch to Side by side to edit");
    }

    /// <summary>
    /// The tabs ship hidden. On an empty page the choice is between two
    /// renderings of nothing, and the one thing to do is get text in.
    /// </summary>
    [Fact]
    public void The_layout_choice_is_hidden_until_there_is_a_diff()
    {
        var page = Read(Page);
        var tabs = Between(page, "data-diff-layout", "</span>");
        tabs.Should().Contain("hidden", "the tool starts with nothing to lay out");

        var js = Code(Read(ViewerJs));
        js.Should().Contain("function setLayoutAvailable(available)");
        js.Should().Contain("setLayoutAvailable(false)",
            because: "Clear both empties the page, and the choice has to go with the diff");
    }

    /// <summary>
    /// A remembered "inline" must not be applied while the tabs are hidden, or
    /// the tool opens on an empty read-only pane with no visible way back to
    /// the panes that take text.
    /// </summary>
    [Fact]
    public void A_remembered_inline_layout_waits_for_something_to_show()
    {
        Code(Read(ViewerJs)).Should()
            .Contain(@"apply(saved === ""inline"" && !tabs.hidden ? ""inline"" : ""side"")");
    }

    /// <summary>
    /// The refresh, and the reason the pane can be rebuilt at all: it is
    /// read-only, so there is no cursor, selection or undo history to preserve
    /// and a fresh mount is the whole update.
    /// </summary>
    [Fact]
    public void A_re_diff_leaves_the_inline_pane_stale_until_it_is_rebuilt()
    {
        var js = Code(Read(ViewerJs));

        js.Should().Contain("function setInlineDocument(payload)");
        js.Should().Contain("inlineDocStale = true;",
            because: "every re-diff moves the document the pane is showing");
        js.Should().Contain("if (inlinePane && !inlineDocStale) return;",
            because: "an up-to-date pane is not remounted on every switch");
        js.Should().Contain("dispose(inlinePane.editorId);",
            because: "the old view has to go before initOne will mount over the same host");
    }

    /// <summary>
    /// Both compare screens now have three editors on the page and one shared
    /// band event. The inline pane's region indices are its own numbering, so a
    /// band clicked there must not drive the side-by-side pair — those would
    /// expand stretches of a layout nobody is looking at, and be waiting like
    /// that on the way back.
    /// </summary>
    [Fact]
    public void A_band_clicked_inline_does_not_move_the_side_by_side_panes()
    {
        var handler = Code(Between(Read(ViewerJs), "function wireCollapseToggle()", "\n}\n"));

        handler.Should().Contain(@"closest('[data-layout-pane=""inline""]')");
        handler.Should().Contain("if (inlinePane) toggleCollapsedRegion(inlinePane.editorId, index);");
        handler.Should().Contain("toggleCollapsedRegion(panes.left.editorId, index)",
            because: "the side-by-side pair still expands together");
    }

    /// <summary>Drops comment-only lines, so a disabled call cannot satisfy a guard.</summary>
    private static string Code(string js) =>
        string.Join('\n', js.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        from.Should().BeGreaterThan(-1, $"'{start}' should exist");
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        to.Should().BeGreaterThan(from, $"'{start}' should be followed by '{end}'");
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
