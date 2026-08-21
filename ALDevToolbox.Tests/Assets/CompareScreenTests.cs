using System.Text.RegularExpressions;
using ALDevToolbox.Components.Shared;
using ALDevToolbox.Services.ObjectExplorer;
using FluentAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the two compare screens — the Object Explorer's file diff and the
/// standalone Compare tool — which PR 14d put on the design system's
/// archetype 11 (<c>.pw</c> frame + <c>.cmp</c> body).
///
/// They are one screen with two sources for the two sides, so they share a
/// frame, a stylesheet and the whole of <c>source-viewer.js</c>. Four things
/// here fail without failing.
///
/// <b>The pane class is a hook and a layout at once.</b> A compare pane is a
/// <c>.source-viewer</c>, which tools.css declares a flex COLUMN via
/// <c>.source-viewer:not(.pw)</c> — two classes' worth of specificity. The
/// compare override has to beat it or the overview ruler stops sitting beside
/// the code and stacks under it, which no build catches and which is exactly
/// what happened when the page shell moved to the design layer.
///
/// <b>The rail's keyline comes from two places.</b> The markup asks
/// <see cref="RowStateIcon.RowClass"/> for a state class; pages-power.css
/// paints <c>.crow.is-*</c>. A state the sheet does not know draws a rail row
/// with no colour and no complaint.
///
/// <b>A drag handle is inert until the JS agrees.</b> <c>data-split="rail"</c>
/// finds its spec by name in <c>SPLIT_SPECS</c>. Name it something the map does
/// not hold and the handle renders, takes the pointer, and does nothing.
///
/// <b>A power tool with short content used to render short.</b>
/// <c>height: 100%</c> on a grid item resolves against its grid AREA, so the
/// shell's auto row made the percentage circular. Every <c>.pw</c> page needs
/// the row stretched; the tall ones (a viewer full of code) hid it for months.
/// </summary>
public sealed class CompareScreenTests
{
    private const string OeCompare = "ALDevToolbox/Components/Pages/ObjectExplorer/OeCompareFile.razor";
    private const string Tool = "ALDevToolbox/Components/Pages/Compare.razor";
    private const string ViewerJs = "ALDevToolbox/wwwroot/source-viewer.js";
    /// <summary>
    /// Both halves of the override live here since PR 17b: the viewer's own
    /// composition moved out of tools.css into a sheet named for it. The
    /// source-order half of this test only means anything while both are in
    /// ONE file - split them and the tie-break becomes the &lt;link&gt; order
    /// instead, which StylesheetLoadOrderTests owns.
    /// </summary>
    private const string Tools = "ALDevToolbox/wwwroot/source-viewer.css";
    private const string PagesPower = "ALDevToolbox/wwwroot/pages-power.css";

    private static readonly string[] Sheets =
    [
        "ALDevToolbox/wwwroot/pages-power.css",
        "ALDevToolbox/wwwroot/components.css",
        "ALDevToolbox/wwwroot/pages.css",
        "ALDevToolbox/wwwroot/tokens.css",
        "ALDevToolbox/wwwroot/app.css",
        "ALDevToolbox/wwwroot/code-editor.css",
        "ALDevToolbox/wwwroot/source-viewer.css",
    ];

    // ── The frame ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(OeCompare)]
    [InlineData(Tool)]
    public void Both_compare_screens_are_the_power_tool_frame(string page)
    {
        // Classes are matched inside a class= attribute, not as bare substrings
        // anywhere in the file: "cmp" is a substring of cmp__vname and of every
        // comment that mentions the archetype, so the loose form stayed green
        // with the .cmp container deleted.
        var markup = StripComments(Read(page));
        foreach (var cls in new[] { "pw", "pw__head", "pw__title", "pw__name", "pw__bar", "pw__body", "pw__foot", "cmp" })
        {
            RenderedClasses(markup).Should().Contain(cls,
                because: $"archetype 11 is the .pw frame around a .cmp body; without .{cls} "
                       + "the page is a lookalike that drifts on the next design change");
        }
    }

    [Theory]
    [InlineData(OeCompare)]
    [InlineData(Tool)]
    public void Neither_compare_screen_keeps_the_pre_port_vocabulary(string page)
    {
        var markup = StripComments(Read(page));
        foreach (var cls in new[] { "oe-compare-file", "compare-page__", "admin-page__header", "form-actions", "section-label" })
        {
            markup.Should().NotContain(cls,
                because: $".{cls} is the legacy page shell archetype 11 replaced");
        }
    }

    [Fact]
    public void The_retired_compare_shells_are_gone_from_every_sheet_and_the_script()
    {
        foreach (var cls in new[] { "oe-compare-file", "compare-page__" })
        {
            foreach (var sheet in Sheets)
            {
                Selectors(Read(sheet)).Should().NotContain(sel => sel.Contains(cls),
                    because: $"source-viewer.css loads after the design layer, so a returning .{cls} "
                           + "would out-specify the archetype without anything failing");
            }
            Read(ViewerJs).Should().NotContain(cls,
                because: "the script toggles state on these pages and would resurrect the class by hand");
        }

        File.Exists(Path.Combine(Root(), "ALDevToolbox/Components/Pages/Compare.razor.css"))
            .Should().BeFalse(because: "the tool's page shell is the design layer's now; "
                                     + "a leftover scoped sheet is a second, invisible source of layout");
    }

    // ── The pane, which is a hook and a layout at once ──────────────────

    [Fact]
    public void The_compare_pane_out_specifies_the_column_layout_it_overrides()
    {
        var tools = Read(Tools);

        var column = Rules(tools).Single(r => r.Selector == ".source-viewer:not(.pw)");
        column.Body.Should().Contain("column");

        // `.source-viewer:not(.pw)` is (0,2,0). A single-class override never
        // lands, whatever the source order — and the failure is silent: the
        // ruler stacks under the code and the page still renders.
        var row = Rules(tools)
            .Where(r => r.Body.Contains("flex-direction: row"))
            .Where(r => r.Selector.Contains("source-viewer--compare"))
            .Should().ContainSingle().Subject;

        Regex.Matches(row.Selector, @"\.[a-z][\w-]*").Count.Should().BeGreaterThanOrEqualTo(2,
            because: "one class cannot beat .source-viewer:not(.pw); "
                   + "the compare pane's own direction has to carry at least two");

        // Weight alone is not the answer: both selectors are (0,2,0), so the
        // one that wins is the one that comes LAST. Asserting only the class
        // count let a plausible tidy-up - moving the compare block up beside
        // the rule it overrides - put the ruler back under the code with every
        // test still green.
        tools.IndexOf(".source-viewer.source-viewer--compare", StringComparison.Ordinal)
            .Should().BeGreaterThan(tools.IndexOf(".source-viewer:not(.pw)", StringComparison.Ordinal),
                because: "the two selectors weigh the same, so source order is the tie-break "
                       + "and the compare pane has to be declared after the column it overrides");
    }

    // ── The rail's keyline, which comes from two places ─────────────────

    [Theory]
    [InlineData("added")]
    [InlineData("removed")]
    [InlineData("modified")]
    public void Every_rail_state_the_page_can_ask_for_has_a_keyline(string status)
    {
        var cls = RowStateIcon.RowClass(status);
        var selectors = Selectors(Read(PagesPower)).ToList();

        // Named part by part, not as a prefix: `.crow.is-removed .crow__g`
        // alone satisfied a `Contains(".crow.is-removed")` check while the
        // keyline - the part that carries the state at a glance - was gone.
        selectors.Should().Contain(sel => sel.Contains($".crow.{cls} .crow__edge"),
            because: $"the rail asks RowStateIcon for \"{cls}\" on a \"{status}\" row; "
                   + "a state the sheet does not paint draws a colourless keyline and says nothing");
        selectors.Should().Contain(sel => sel.Contains($".crow.{cls} .crow__g"),
            because: "the letter and the keyline are two renderings of one fact");
    }

    [Theory]
    [InlineData("new")]
    [InlineData("modified")]
    [InlineData("removed")]
    public void The_lone_glyph_outside_a_rail_row_carries_its_own_colour(string state)
    {
        // `.crow.is-* .crow__g` only paints a glyph INSIDE a row. The view bar's
        // is on its own, so it takes a `crow__g--*` modifier - three rules that
        // nothing else references and that no other assertion touched. Delete
        // them and the letter goes grey with sixteen tests still green.
        Selectors(Read(PagesPower)).Should().Contain(sel => sel.Contains($".crow__g--{state}"),
            because: "the view bar's change letter has no row to inherit a tint from");
        Read(OeCompare).Should().Contain("crow__g--",
            because: "the page is the only thing that uses the modifier");
    }

    [Fact]
    public void The_rail_glyph_and_the_keyline_cover_the_same_three_words()
    {
        var markup = Read(OeCompare);
        var arm = markup[markup.IndexOf("private static string StatusGlyph", StringComparison.Ordinal)..];
        arm = arm[..arm.IndexOf("};", StringComparison.Ordinal)];

        // The letter and the colour are two renderings of one fact. Both are
        // driven off the comparer's own words, so both lists have to hold them.
        foreach (var status in new[] { "added", "removed" })
        {
            arm.Should().Contain($"\"{status}\"",
                because: $"the comparer emits \"{status}\"; an unlisted state falls through to M");
        }
    }

    // ── The script contract ────────────────────────────────────────────

    [Fact]
    public void Every_drag_handle_names_a_split_the_script_knows()
    {
        var js = Read(ViewerJs);
        // Bounded at the literal's closing brace. Scanning to EOF meant any
        // future four-space-indented `key: {` anywhere in three thousand lines
        // silently widened the allow-list.
        var from = js.IndexOf("const SPLIT_SPECS", StringComparison.Ordinal);
        from.Should().BeGreaterThan(-1);
        var to = js.IndexOf("\n};", from, StringComparison.Ordinal);
        to.Should().BeGreaterThan(from);
        var specs = Regex.Matches(js[from..to],
                @"^\s{4}(?<key>[a-z]+):\s*\{", RegexOptions.Multiline)
            .Select(m => m.Groups["key"].Value)
            .ToHashSet();
        specs.Should().NotBeEmpty();

        foreach (var page in RazorPages())
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(page), @"data-split=""(?<name>[a-z]+)"""))
            {
                specs.Should().Contain(m.Groups["name"].Value,
                    because: $"{Path.GetFileName(page)} renders a resize handle named "
                           + $"\"{m.Groups["name"].Value}\"; a name SPLIT_SPECS does not hold makes a "
                           + "handle that renders, takes the pointer and does nothing");
            }
        }
    }

    [Fact]
    public void The_tool_still_carries_every_hook_its_script_reaches_for()
    {
        var markup = Read(Tool);
        foreach (var hook in new[] { "data-compare-summary", "data-compare-swap", "data-compare-clear", "data-diff-nav" })
        {
            markup.Should().Contain(hook,
                because: $"source-viewer.js wires the tool through [{hook}] and no-ops without it");
        }
    }

    [Fact]
    public void The_rail_filter_and_its_empty_line_are_spelled_the_same_in_both_places()
    {
        var markup = Read(OeCompare);
        var js = Read(ViewerJs);
        // data-* attributes, not classes. A styling-shaped name that only
        // JavaScript reads is the #562 trap from the other side: the next
        // person retiring CSS finds a class with no rule and takes it.
        foreach (var hook in new[] { "data-rail-filter", "data-rail-empty" })
        {
            markup.Should().Contain(hook);
            js.Should().Contain(hook,
                because: "the filter is the only thing that reads the rail; "
                       + "a renamed hook leaves a search box that does nothing");
        }
        RenderedClasses(StripComments(markup)).Should().NotContain(c => c.StartsWith("oe-compare-filter"),
            because: "the hook moved to a data-* attribute; a class-shaped twin invites the trap back");
    }

    [Fact]
    public void The_state_the_script_toggles_is_painted()
    {
        // The summary read-out turns red when a re-diff cannot run. It is the
        // one class the compare script adds by hand.
        Read(ViewerJs).Should().Contain("classList.toggle(\"is-error\"");
        Selectors(Read(PagesPower)).Should().Contain(sel => sel.Contains(".cmp__vname.is-error"));
    }

    [Fact]
    public void The_retired_fill_marker_left_no_selector_behind()
    {
        // PR 14d moved both compare pages onto `.pw`, which took the last two
        // users of the `.u-fill` marker class with it. The marker never had a
        // rule of its own - only `:has(> .u-fill)` read it - so a leftover
        // selector is a second, unreachable opt-in for the property the rule
        // below already sets, sitting under a comment naming three pages that
        // no longer opt in that way.
        foreach (var sheet in Sheets)
        {
            Selectors(Read(sheet)).Should().NotContain(sel => sel.Contains("u-fill"));
        }
        foreach (var page in RazorPages())
        {
            File.ReadAllText(page).Should().NotContain("u-fill",
                because: $"{Path.GetFileName(page)} would be opting in through a mechanism that is gone");
        }
    }

    // ── The frame's height, which was circular ──────────────────────────

    [Fact]
    public void A_power_tool_fills_the_shell_however_short_its_content()
    {
        var rule = Rule(Read(PagesPower), ".app__content-inner:has(.pw)");
        rule.Should().NotBeNull();
        rule.Should().Contain("align-content: stretch",
            because: "`height: 100%` on a grid item resolves against its grid AREA, and the "
                   + "shell's implicit row is auto - so without stretching the row the "
                   + "percentage is circular and a short tool renders at its content height "
                   + "inside a full-height shell");
    }

    // ── The link the rail's way out depends on ──────────────────────────

    [Fact]
    public void The_capped_rail_links_to_the_full_change_list()
    {
        var url = new ObjectExplorerLinks().ReleaseCompare(900, 901);

        // The Release page reads `scope` with Enum.TryParse and validates
        // `right` against the releases its picker offers, so both spellings
        // have to match or the link lands on the object search instead.
        url.Should().Be("/object-explorer/release/900?scope=Compare&right=901");
        Read("ALDevToolbox/Components/Pages/ObjectExplorer/OeReleaseDetail.razor")
            .Should().Contain("query[\"right\"]");
    }

    // ── helpers ────────────────────────────────────────────────────────

    private static IEnumerable<string> RazorPages() =>
        Directory.EnumerateFiles(
            Path.Combine(Root(), "ALDevToolbox", "Components"), "*.razor", SearchOption.AllDirectories);

    /// <summary>Razor comments, so a class named only in prose never counts.</summary>
    private static string StripComments(string razor) =>
        Regex.Replace(razor, @"@\*.*?\*@", "", RegexOptions.Singleline);

    /// <summary>Every class actually placed in a `class=` attribute.</summary>
    private static IEnumerable<string> RenderedClasses(string markup) =>
        Regex.Matches(markup, @"class=""(?<v>[^""]*)""")
            .SelectMany(m => Regex.Replace(m.Groups["v"].Value, @"@\([^)]*\)", " ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(c => c.Trim())
            .Where(c => c.Length > 0 && !c.StartsWith('@'));

    private static IEnumerable<(string Selector, string Body)> Rules(string css)
    {
        var stripped = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        foreach (Match m in Regex.Matches(stripped, @"(?<sel>[^{}@]+)\{(?<body>[^{}]*)\}"))
        {
            yield return (m.Groups["sel"].Value.Trim(), m.Groups["body"].Value);
        }
    }

    /// <summary>
    /// A label binds to the nearest control. The view bar used to carry
    /// "Ctrl Down next change" immediately left of the two nav buttons, which
    /// put those words against the PREVIOUS one and read backwards; the same
    /// pair was already spelled out in the foot, in the same order as the
    /// arrows. So no shortcut hint may appear before the nav buttons - the
    /// buttons' own titles cover them where they sit.
    /// </summary>
    [Fact]
    public void No_shortcut_hint_sits_beside_the_change_nav_buttons()
    {
        var page = Read(OeCompare);

        var firstHint = page.IndexOf("kbd-hint", StringComparison.Ordinal);
        var nav = page.IndexOf(@"data-diff-nav=""next""", StringComparison.Ordinal);

        nav.Should().BeGreaterThan(0);
        firstHint.Should().BeGreaterThan(nav,
            because: "every shortcut hint belongs in the foot, below the buttons, not beside them");

        page.Should().Contain(@"title=""Jump to the next change (Ctrl + Down arrow)""");
        page.Should().Contain(@"title=""Jump to the previous change (Ctrl + Up arrow)""");
    }

    private static IEnumerable<string> Selectors(string css) => Rules(css).Select(r => r.Selector);

    private static string? Rule(string css, string selector) =>
        Rules(css).FirstOrDefault(r => r.Selector.Split(',')
            .Any(s => s.Trim() == selector)).Body;

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
