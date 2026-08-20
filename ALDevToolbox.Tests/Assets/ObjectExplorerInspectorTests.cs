using System.Text.RegularExpressions;
using FluentAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the Object Explorer inspector's port onto the handoff's power-tool
/// pane. Three things here fail silently rather than loudly.
///
/// <b>Load order.</b> <c>tools.css</c> loads after <c>pages-power.css</c>, so
/// any legacy rule the new markup still matches quietly wins over the design
/// layer. The port therefore renames the viewer's behaviour hooks to
/// <c>sv-*</c> and leaves the <c>source-viewer__outline-*</c> family to
/// <c>SourceFileViewerLegacy.razor</c>, which is still reachable behind
/// <c>OBJECT_EXPLORER_LEGACY_VIEWER=1</c> and still needs its own CSS.
///
/// <b>Class names built in JavaScript.</b> The references, find and dependency
/// lists are rendered client-side, so a typo in a design class name produces an
/// unstyled row rather than a build error.
///
/// <b>The <c>hidden</c> attribute.</b> Every component in the design layer sets
/// an explicit <c>display</c>, which beats the user agent's
/// <c>[hidden] { display: none }</c>. components.css carries one guard for all
/// of them — and a panel that relies on a class to show itself must then not
/// also carry <c>hidden</c>, or the guard wins and the panel never appears.
/// </summary>
public sealed class ObjectExplorerInspectorTests
{
    private const string Viewer = "ALDevToolbox/Components/Pages/ObjectExplorer/SourceFileViewer.razor";
    private const string LegacyViewer = "ALDevToolbox/Components/Pages/ObjectExplorer/SourceFileViewerLegacy.razor";
    private const string ViewerJs = "ALDevToolbox/wwwroot/source-viewer.js";

    /// <summary>
    /// The legacy outline classes that still carry rules in tools.css. The new
    /// viewer must not render any of them, or those rules override the design
    /// layer it was just ported onto.
    /// </summary>
    private static readonly string[] LegacyOutlineClasses =
    [
        "source-viewer__outline-section-toggle",
        "source-viewer__outline-section-chevron",
        "source-viewer__outline-section-title",
        "source-viewer__outline-section-count",
        "source-viewer__outline-list",
        "source-viewer__outline-item",
        "source-viewer__outline-link",
        "source-viewer__outline-name",
        "source-viewer__outline-sig",
        "source-viewer__outline-line",
        "source-viewer__outline-filter",
        "source-viewer__find-list",
        "source-viewer__find-snippet",
    ];

    [Fact]
    public void The_ported_viewer_renders_none_of_the_legacy_outline_classes()
    {
        var markup = Read(Viewer);
        var js = Read(ViewerJs);
        foreach (var cls in LegacyOutlineClasses)
        {
            markup.Should().NotContain(cls,
                because: $"tools.css loads after pages-power.css, so a stray .{cls} out-specifies the port");
            js.Should().NotContain(cls,
                because: $"the client-side renderers feed the same panel as the markup");
        }
    }

    [Fact]
    public void The_legacy_viewer_still_has_the_rules_it_renders()
    {
        var legacy = Read(LegacyViewer);
        var css = Read("ALDevToolbox/wwwroot/tools.css");
        var used = LegacyOutlineClasses.Where(cls => legacy.Contains(cls)).ToList();
        used.Should().NotBeEmpty(because: "the legacy viewer is what those rules are still there for");
        foreach (var cls in used)
        {
            // A bare `.cls { }` rule, not merely a selector mentioning it:
            // `.cls::placeholder` surviving on its own would leave the element
            // with no styling at all, which is the failure this guards.
            // `source-viewer__outline-section` is deliberately not in the list
            // above — it carries no rule of its own, only combinators.
            Rule(css, "." + cls).Should().NotBeNull(
                because: $"{Path.GetFileName(LegacyViewer)} still renders .{cls}");
        }
    }

    /// <summary>
    /// The handoff components the inspector is built out of. Each needs its own
    /// rule in the design layer — satisfying the name from <c>tools.css</c>
    /// instead would mean the port had quietly grown a private copy of a shared
    /// component, which is the thing this whole migration is undoing.
    /// </summary>
    [Theory]
    [InlineData("refhit")]
    [InlineData("refhit__n")]
    [InlineData("refhit__c")]
    [InlineData("refgrp")]
    [InlineData("refgrp__h")]
    [InlineData("refgrp__n")]
    [InlineData("refs")]
    [InlineData("orow")]
    [InlineData("orow__glyph")]
    [InlineData("orow__name")]
    [InlineData("orow__type")]
    [InlineData("olist")]
    [InlineData("pane")]
    [InlineData("pane__head")]
    [InlineData("pane__body")]
    [InlineData("pane__sec")]
    [InlineData("pane__sec-h")]
    [InlineData("pane__count")]
    [InlineData("pill-tab")]
    [InlineData("pill-tab__count")]
    [InlineData("symcard")]
    [InlineData("symcard__sig")]
    [InlineData("symcard__meta")]
    [InlineData("symcard__acts")]
    [InlineData("kbd")]
    [InlineData("otree__caret")]
    [InlineData("refgrp__name")]
    [InlineData("refgrp__rows")]
    [InlineData("orow--child")]
    public void Every_handoff_component_the_inspector_uses_is_in_the_design_layer(string cls)
    {
        var design = DesignSheets.Any(f => Rule(Read(f), "." + cls) is not null);
        design.Should().BeTrue(
            because: $".{cls} comes from the handoff, so its rule belongs in the design layer");
    }

    /// <summary>
    /// The references, find and dependency rows are built in JavaScript, so a
    /// mistyped class name is not a build error — it is an unstyled row nobody
    /// notices until someone looks. Checked per <c>className</c> literal rather
    /// than per name: <c>"muted source-viewer__refs-empty"</c> is fine because
    /// <c>.muted</c> styles it and the second name is only a query handle, but
    /// a lone <c>"refhitt"</c> leaves the element with nothing at all.
    /// </summary>
    [Fact]
    public void Every_element_the_client_renderers_build_is_styled()
    {
        var js = Read(ViewerJs);
        var groups = Regex.Matches(js, @"className = ""([^""]+)""")
            .Select(m => m.Groups[1].Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                // `is-*` are state modifiers; they only ever appear compounded
                // onto another class, so there is no bare rule to find.
                .Where(cls => !cls.StartsWith("is-", StringComparison.Ordinal))
                .ToList())
            .Where(g => g.Count > 0 && !g.All(BehaviourOnly.Contains))
            .ToList();
        groups.Should().NotBeEmpty();

        var sheets = DesignSheets
            .Concat(["ALDevToolbox/wwwroot/tools.css", "ALDevToolbox/wwwroot/base.css"])
            .Select(Read)
            .ToList();
        foreach (var group in groups)
        {
            group.Any(cls => sheets.Any(css => Selectors(css)
                    .Any(sel => Regex.IsMatch(sel, $@"\.{Regex.Escape(cls)}(?![\w-])"))))
                .Should().BeTrue(
                    because: $"source-viewer.js renders class=\"{string.Join(' ', group)}\" and nothing styles it");
        }
    }

    /// <summary>
    /// Class names the viewer sets purely as a query handle, on an element that
    /// nothing else styles either. Keep this list short and say why: the
    /// default is that a class the renderer sets is a class somebody meant to
    /// style.
    /// </summary>
    private static readonly HashSet<string> BehaviourOnly =
    [
        // The label inside the busy toast. The toast itself sets colour, font
        // and layout on the flex parent; the span only exists so the text can
        // be replaced without touching the spinner beside it.
        "source-viewer__busy-text",
    ];

    private static readonly string[] DesignSheets =
    [
        "ALDevToolbox/wwwroot/pages-power.css",
        "ALDevToolbox/wwwroot/components.css",
        "ALDevToolbox/wwwroot/pages.css",
    ];

    /// <summary>
    /// `sv-row` is the hook the outline filter scans for. A renderer that emits
    /// an outline row without it produces a section holding zero matchable
    /// rows — which the filter then hides and, because the empty-needle escape
    /// used to live inside the per-row loop, never brought back. That shipped
    /// once: the dependency rows were renamed to `.orow` and lost the hook.
    /// </summary>
    [Fact]
    public void Every_outline_row_the_client_builds_carries_the_filter_hook()
    {
        var js = Read(ViewerJs);
        // Any string literal that names the `orow` class — the assignment is a
        // ternary, so this is not always `className = "..."`.
        var rowClasses = Regex.Matches(js, @"""([^""]*)""")
            .Select(m => m.Groups[1].Value)
            .Where(v => v.Split(' ').Contains("orow"))
            .ToList();
        rowClasses.Should().NotBeEmpty(because: "buildDepsRow builds .orow rows");
        rowClasses.Should().OnlyContain(cls => cls.Split(' ').Contains("sv-row"));
    }

    /// <summary>
    /// Clearing the filter has to restore everything, including a section that
    /// holds no rows at all (an empty Used-by, a list still loading). Deciding
    /// visibility per row cannot do that, because the loop never runs.
    /// </summary>
    [Fact]
    public void Both_filters_short_circuit_on_an_empty_needle()
    {
        var js = Read(ViewerJs);
        foreach (var fn in new[] { "function wireOutlineFilter", "function wireRefsFilter" })
        {
            var start = js.IndexOf(fn, StringComparison.Ordinal);
            start.Should().BeGreaterThan(-1);
            var body = js[start..js.IndexOf("\n}", start, StringComparison.Ordinal)];
            var escape = body.IndexOf("needle.length === 0", StringComparison.Ordinal);
            var matching = body.IndexOf("let anyVisible", StringComparison.Ordinal);
            escape.Should().BeGreaterThan(-1, because: $"{fn} has to handle a cleared box");
            matching.Should().BeGreaterThan(-1);
            escape.Should().BeLessThan(matching,
                because: $"{fn} must restore every section before it starts matching rows");
        }
    }

    [Fact]
    public void Components_css_guards_the_hidden_attribute()
    {
        var css = Read("ALDevToolbox/wwwroot/components.css");
        Selectors(css).Should().Contain("[hidden]",
            because: "every component here sets an explicit display, which beats the UA rule");
        Rule(css, "[hidden]").Should().MatchRegex(@"display:\s*none\s*!important",
            because: "without !important the guard loses to any component's own display");
    }

    /// <summary>
    /// The panel switch is class-driven, so a panel that can be the default
    /// view must not also carry <c>hidden</c> — with the guard above in place
    /// the attribute wins and the panel renders blank. That shipped once, to
    /// the shortcuts panel, which has since been retired in favour of the
    /// status line; the rule outlives it, so this asserts the rule rather than
    /// naming a panel.
    /// </summary>
    [Fact]
    public void A_panel_that_can_be_the_default_view_is_shown_by_its_class_alone()
    {
        var markup = Read(Viewer);
        var panels = Regex.Matches(markup, @"<div class=""source-viewer__panel[^""]*""[^>]*>")
            .Select(m => m.Value)
            .ToList();
        panels.Should().NotBeEmpty();

        // A *bare* `hidden`, not `hidden="@(...)"`. The references panel carries
        // a computed one and is only `is-active` in the same case that makes it
        // false; an unconditional attribute is the one that cannot be right.
        foreach (var panel in panels.Where(p => p.Contains("is-active")))
        {
            Regex.IsMatch(panel, @"\shidden(?![-=\w])").Should().BeFalse(
                because: $"[hidden] is !important and beats .is-active, in: {panel}");
        }

        // The panels whose tab is conditional keep it: hidden is how the tab
        // controller says "this view does not exist yet".
        markup.Should().MatchRegex(@"data-panel=""find""[^>]*hidden",
            because: "the Find panel has no content until a search runs");
    }

    /// <summary>
    /// The glyph column draws exactly the three characters the handoff draws.
    /// An earlier version invented five more and a fresh-eyes review found
    /// them undecodable — a single character can only be a mnemonic for a word
    /// the reader can guess from it. This pins the set in both directions so
    /// the next kind that lands does not quietly acquire an invented letter.
    /// </summary>
    [Fact]
    public void The_glyph_column_draws_only_the_three_the_handoff_draws()
    {
        var markup = Read(Viewer);
        var body = Body(markup, "string KindGlyph(string kind)");
        var glyphs = Regex.Matches(body, @"=>\s*""((?:[^""\\]|\\.)+)""")
            .Select(m => m.Groups[1].Value)
            .ToList();
        glyphs.Should().BeEquivalentTo(["#", "t", "f"],
            because: "the handoff's .orow__glyph is # field, t trigger, f procedure — and nothing else");
        body.Should().Contain("_ => string.Empty",
            because: "every other kind draws a blank rather than an invented letter");
    }

    /// <summary>
    /// A tint on an empty span accents nothing, so the two switches have to
    /// agree on which kinds carry a glyph at all.
    /// </summary>
    [Fact]
    public void Only_a_kind_with_a_glyph_carries_a_tint()
    {
        var markup = Read(Viewer);
        var glyphed = KindsIn(markup, "KindGlyph");
        var tinted = KindsIn(markup, "KindGlyphClass");
        tinted.Should().NotBeEmpty();
        tinted.Should().BeSubsetOf(glyphed,
            because: "colouring a kind that renders no character accents an empty box");
    }

    /// <summary>The text of one switch expression, signature to closing "};".</summary>
    private static string Body(string markup, string signature)
    {
        var start = markup.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, because: $"{signature} is declared in the page");
        var end = markup.IndexOf("};", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return markup[start..end];
    }

    /// <summary>The kind strings matched by one switch expression.</summary>
    private static List<string> KindsIn(string markup, string methodName)
    {
        var start = markup.IndexOf($"string {methodName}(string kind)", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, because: $"{methodName} is declared in the page");
        var end = markup.IndexOf("};", start, StringComparison.Ordinal);
        return Regex.Matches(markup[start..end], @"""([a-z_]+)""\s*(?:or\s*""[a-z_]+""\s*)*=>")
            .SelectMany(m => Regex.Matches(m.Value, @"""([a-z_]+)""").Select(x => x.Groups[1].Value))
            .Distinct()
            .ToList();
    }

    private static IEnumerable<string> Selectors(string css)
    {
        var stripped = Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Matches(stripped, @"([^{}]+)\{")
            .Select(m => Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim())
            .Where(sel => sel.Length > 0 && !sel.StartsWith('@'));
    }

    private static string? Rule(string css, string selector)
    {
        var stripped = Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        foreach (Match m in Regex.Matches(stripped, @"([^{}]+)\{([^{}]*)\}"))
        {
            if (m.Groups[1].Value.Split(',').Any(s => s.Trim() == selector)) return m.Groups[2].Value;
        }
        return null;
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(Root(), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ALDevToolbox.slnx")))
        {
            dir = dir.Parent;
        }
        dir.Should().NotBeNull(because: "the tests run from inside the repo");
        return dir!.FullName;
    }
}
