using FluentAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Where a keyboard shortcut is written down in the source viewer, and where
/// it deliberately is not.
///
/// The hover card used to print <c>Ctrl</c> <c>click</c> and <c>Shift</c>
/// <c>F12</c> on its two buttons. That put the same two facts in front of the
/// reader on every hover — hundreds of times, to be learned once — while the
/// footer already carries both permanently (#566 moved them there precisely so
/// they would be always visible). No IDE does this: VS Code and Zed put nothing
/// in the hover, and a shortcut belongs on the menu item that performs the
/// action.
///
/// It was not only noise. The two chips were the widest thing in the card, and
/// they were what pushed its contents 38px past its own frame.
/// </summary>
public sealed class SymbolShortcutTests
{
    private const string ViewerJs = "ALDevToolbox/wwwroot/source-viewer.js";
    private const string EditorJs = "ALDevToolbox/wwwroot/code-editor.js";
    private const string PagesPower = "ALDevToolbox/wwwroot/pages-power.css";
    private const string Viewer = "ALDevToolbox/Components/Pages/ObjectExplorer/SourceFileViewer.razor";

    [Fact]
    public void The_hover_card_prints_no_shortcuts()
    {
        var card = Code(Between(Read(ViewerJs), "function buildSymbolCard(", "\n}\n"));

        card.Should().NotContain("kbd", because: "the card carries the actions, not the key to them");
        card.Should().Contain(@"go.append(""Go to definition"")", "the actions themselves stay");
        card.Should().Contain(@"refs.append(""Find references"")");
    }

    /// <summary>
    /// The card is the handoff's 356px again. It was widened to 412px to hold
    /// the chips, which was a real fix to the wrong problem — the container was
    /// right and the contents were not.
    /// </summary>
    [Fact]
    public void The_card_is_back_to_the_width_the_handoff_drew()
    {
        Read(PagesPower).Should().Contain(".symcard { position: absolute; z-index: 6; width: 356px;");
    }

    /// <summary>
    /// A grid item's automatic minimum size is its min-content, and one item
    /// wider than the card opens the column for every other — which is why the
    /// signature, the description and the buttons all overflowed by an
    /// identical amount when only the meta row was too wide.
    /// </summary>
    [Fact]
    public void The_meta_row_can_shrink_so_it_cannot_push_the_card_open()
    {
        Read(PagesPower).Should().Contain(
            ".symcard__meta { display: flex; align-items: center; gap: var(--space-2); min-width: 0;");
    }

    /// <summary>
    /// The actions are text, not buttons. Two full-width outline buttons took
    /// 37% of the height of a card whose job is to say what a symbol is; as
    /// links they take 23% and the card is 14px shorter on every symbol.
    ///
    /// The keyline above them stays. It is what keeps the actions readable as
    /// actions — without it they sit against the meta line and read as a third
    /// row of metadata, which was the version that lost.
    ///
    /// Semantics are unchanged and load-bearing: go-to-definition is an anchor
    /// so it can be opened in a new tab, find-references is a button because it
    /// mints a session rather than navigating. Styling them alike must not make
    /// them the same element.
    /// </summary>
    [Fact]
    public void The_card_offers_its_actions_as_text_not_buttons()
    {
        var sheet = Read(PagesPower);
        sheet.Should().Contain(".symcard__act { padding: 0; border: 0; background: transparent;");
        sheet.Should().Contain(".symcard__acts { display: flex;");
        sheet.Should().Contain("border-top: 1px solid var(--border); }",
            because: "the keyline is what separates the actions from the facts above them");

        var card = Code(Between(Read(ViewerJs), "function buildSymbolCard(", "\n}\n"));
        card.Should().NotContain(@"""btn btn--sm""", "the card's actions are no longer buttons");
        card.Should().Contain(@"go.className = ""symcard__act""");
        card.Should().Contain(@"refs.className = ""symcard__act""");
        card.Should().Contain(@"document.createElement(""a"")",
            because: "go-to-definition stays an anchor so it can open in a new tab");
        card.Should().Contain(@"document.createElement(""button"")",
            because: "find-references mints a session rather than navigating");
    }

    [Fact]
    public void The_right_click_menu_names_the_gestures_that_have_one()
    {
        var js = Code(Read(EditorJs));

        js.Should().Contain(@"keys: [""Shift"", ""F12""]");
        js.Should().Contain(@"keys: [usesCommandKey() ? ""Cmd"" : ""Ctrl"", ""click""]");
        js.Should().Contain(@"keys.className = ""cm-symbol-menu__keys""");

        Read("ALDevToolbox/wwwroot/code-editor.css").Should()
            .Contain("margin-left: auto", "a shortcut sits at the far edge of its menu item");
    }

    /// <summary>
    /// "Find in this file" must stay bare. The footer advertises Ctrl+F under
    /// that same wording for a DIFFERENT feature — CodeMirror's search box —
    /// while this menu item runs a server-side occurrence search. Putting the
    /// footer's chip on this item would send the reader to the wrong thing,
    /// which is worse than telling them nothing.
    /// </summary>
    [Fact]
    public void The_occurrence_search_advertises_no_shortcut()
    {
        var item = Between(Read(EditorJs), @"label: ""Find in this file""", "});");
        item.Should().NotContain("keys:");
    }

    /// <summary>
    /// The footer is the always-visible reference, and it is the reason the
    /// card can drop them: both gestures are on screen at all times.
    /// </summary>
    [Fact]
    public void The_footer_still_carries_both_gestures()
    {
        var foot = Between(Read(Viewer), @"<div class=""pw__foot"">", "</div>");

        foot.Should().Contain(@"<span class=""kbd-hint__label"">definition</span>");
        foot.Should().Contain(@"<span class=""kbd-hint__label"">references</span>");
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
