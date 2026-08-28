using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the row-action menus, which PR 15b (#529) moved off a native
/// <c>&lt;details&gt;</c> with a private <c>tools.css</c> dialect onto the design
/// system's <c>.ra</c> / <c>.ra__menu</c> / <c>.menu</c>.
///
/// <c>.ra__menu</c> had named two different things — the absolutely-positioned
/// popup in <c>components.css</c>, the <c>&lt;details&gt;</c> wrapper here — and
/// <c>tools.css</c> loads second, so the app ran with a comment-documented reset
/// of <c>position</c>, <c>display</c>, <c>top</c>, <c>right</c> and
/// <c>z-index</c> holding every kebab in place. That was the collision the
/// whole issue was about, and it is now one name meaning one thing.
///
/// Four ways this breaks without anything failing:
///
/// <b>The markup and the script agree by convention only.</b> The component
/// renders <c>data-ra-toggle</c>, <c>.ra</c>, <c>.ra__menu</c> and expects
/// <c>row-actions-menu.js</c> to toggle <c>is-open</c>. Rename any of the four
/// on either side and the trigger renders, takes the pointer and does nothing —
/// the exact shape of the trap #562 was filed for.
///
/// <b><c>display: none</c> looks redundant beside <c>.ra.is-open</c>.</b> Delete
/// it and every menu on the page renders open, stacked over the rows beneath
/// them. Nothing errors; the page just looks broken in a way a class-name
/// audit cannot see.
///
/// <b>A half-migrated call site keeps its <c>&lt;details&gt;</c>.</b> It would
/// open natively, never gain <c>is-open</c>, and so never get the popup rules —
/// rendering the menu in flow, pushing the row apart.
///
/// <b>A menu with no toggle can never open.</b> There is no other affordance;
/// the row simply loses its actions.
/// </summary>
public sealed class RowActionsMenuTests
{
    private const string Script = "ALDevToolbox/wwwroot/row-actions-menu.js";
    private const string Components = "ALDevToolbox/wwwroot/components.css";

    private static readonly string[] Sheets =
    [
        "ALDevToolbox/wwwroot/components.css",
        "ALDevToolbox/wwwroot/app.css",
        "ALDevToolbox/wwwroot/code-editor.css",
        "ALDevToolbox/wwwroot/source-viewer.css",
        "ALDevToolbox/wwwroot/shell.css",
        "ALDevToolbox/wwwroot/pages.css",
        "ALDevToolbox/wwwroot/pages-forms.css",
        "ALDevToolbox/wwwroot/pages-content.css",
        "ALDevToolbox/wwwroot/pages-power.css",
    ];

    /// <summary>The legacy dialect, all of which #529 deleted.</summary>
    private static readonly string[] Retired =
        ["ra__pop", "ra__item", "ra__divider", "ra__solo", "ra__sub", "ra__subcaret"];

    // ── Nothing left of the old dialect ────────────────────────────────

    [Fact]
    public void The_legacy_row_action_dialect_is_gone_from_every_sheet()
    {
        foreach (var cls in Retired)
        {
            foreach (var sheet in Sheets)
            {
                Selectors(Read(sheet)).Should().NotContain(sel => sel.Contains(cls),
                    because: $".{cls} was the private dialect #529 retired; the later sheets load after "
                           + "the design layer, so a returning rule would out-specify the system's");
            }
        }
    }

    [Fact]
    public void No_component_still_renders_the_legacy_dialect()
    {
        foreach (var file in Razors())
        {
            var classes = RenderedClasses(StripComments(File.ReadAllText(file))).ToHashSet();
            classes.Overlaps(Retired).Should().BeFalse(
                because: $"{Relative(file)} names a class with no rules left, so its menu renders "
                       + "as an unstyled column of links in the middle of the row");
        }
    }

    [Fact]
    public void No_row_action_menu_is_still_a_native_disclosure()
    {
        foreach (var file in Razors())
        {
            var markup = StripComments(File.ReadAllText(file));
            if (!RenderedClasses(markup).Contains("ra"))
            {
                continue;
            }

            markup.Should().NotContain("<details",
                because: $"{Relative(file)} would open natively and never gain `is-open`, so the "
                       + "popup rules never apply and the menu renders in flow inside the row");
        }
    }

    // ── The markup / script contract ───────────────────────────────────

    [Theory]
    [InlineData("data-ra-toggle")]
    [InlineData(".ra__menu")]
    [InlineData("is-open")]
    public void The_script_still_speaks_the_markup_s_vocabulary(string token)
    {
        Read(Script).Should().Contain(token,
            because: "the component renders it and nothing else opens the menu; drop it from the "
                   + "script and the trigger becomes a button that takes the pointer and does nothing");
    }

    [Fact]
    public void Every_ra_carries_exactly_one_toggle_and_one_menu()
    {
        foreach (var file in Razors())
        {
            var markup = StripComments(File.ReadAllText(file));
            var wrappers = Regex.Matches(markup, @"class=""ra""").Count;
            if (wrappers == 0)
            {
                continue;
            }

            Regex.Matches(markup, @"data-ra-toggle").Count.Should().Be(wrappers,
                because: $"{Relative(file)} has {wrappers} .ra wrapper(s); a wrapper with no "
                       + "toggle has no affordance at all, and two would fight over is-open");

            Regex.Matches(markup, @"class=""ra__menu menu""").Count.Should().Be(wrappers,
                because: $"{Relative(file)} would toggle `is-open` on a wrapper holding nothing "
                       + "to show — .ra__menu is the popup and .menu is its surface, and the "
                       + "design system's own screens always pair them");
        }
    }

    // ── The rule that looks redundant ──────────────────────────────────

    [Fact]
    public void The_popup_is_hidden_until_the_wrapper_opens()
    {
        Rule(Read(Components), ".ra__menu").Should()
            .NotBeNull(because: ".ra__menu is the popup, and it has to start hidden")
            .And.Contain("display: none",
                because: "without it every row on the page renders its menu open, stacked over "
                       + "the rows beneath — which looks like a data bug, not a CSS one");

        Rule(Read(Components), ".ra.is-open .ra__menu").Should()
            .NotBeNull(because: "this is the only thing that shows a menu; the script sets no styles")
            .And.Contain("display: block");
    }

    [Fact]
    public void A_menu_entry_may_be_a_link()
    {
        Rule(Read(Components), ".menu__item").Should().Contain("text-decoration: none",
            because: "half our entries navigate, so they are anchors — the design system's own "
                   + "screens only ever show buttons, which is why nothing had turned the "
                   + "underline off");
    }

    // ── Helpers (mirroring CompareScreenTests) ─────────────────────────

    private static IEnumerable<string> Razors() =>
        Directory.EnumerateFiles(Path.Combine(Root(), "ALDevToolbox/Components"), "*.razor",
            SearchOption.AllDirectories);

    private static string Relative(string full) =>
        Path.GetRelativePath(Root(), full).Replace('\\', '/');

    private static string StripComments(string razor) =>
        Regex.Replace(razor, @"@\*.*?\*@", "", RegexOptions.Singleline);

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
