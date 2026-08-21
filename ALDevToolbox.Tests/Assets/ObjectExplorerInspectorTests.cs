using System.Text.RegularExpressions;
using FluentAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the Object Explorer inspector's port onto the handoff's power-tool
/// pane. Three things here fail silently rather than loudly.
///
/// <b>Load order.</b> <c>tools.css</c> loads after <c>pages-power.css</c>, so
/// any legacy rule the new markup still matches quietly wins over the design
/// layer. That is why the port renamed the viewer's behaviour hooks to
/// <c>sv-*</c> rather than reuse the pre-#161 names. The
/// <c>source-viewer__outline-*</c> family those names belonged to is gone
/// (#562 retired the second viewer and the CSS it stranded), and the guard
/// below is what stops it coming back.
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
    private const string ViewerJs = "ALDevToolbox/wwwroot/source-viewer.js";
    /// <summary>
    /// The pre-#161 viewer's outline vocabulary. It is gone — the second viewer
    /// and the ~240 lines of tools.css it stranded were retired in #562 — and
    /// this is what stops it coming back.
    ///
    /// It matters because tools.css loads *after* pages-power.css, so any one of
    /// these names re-appearing in the sheet would silently out-specify the
    /// design layer the viewer was ported onto. That is not hypothetical: it is
    /// why PR 14a renamed the ported viewer's hooks to `sv-*` rather than reuse
    /// the old names.
    /// </summary>
    private static readonly string[] RetiredOutlineClasses =
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
        // NOT source-viewer__outline-menu / -menu-item: same prefix, but the
        // live source-viewer.js builds that right-click menu. #562's first pass
        // retired them on the strength of the name and
        // Every_element_the_client_renderers_build_is_styled caught it.
        "source-viewer__find-list",
        "source-viewer__find-snippet",
        "source-viewer__layout",
    ];

    [Fact]
    public void The_retired_outline_vocabulary_is_gone_from_the_markup_and_the_sheet()
    {
        var markup = Read(Viewer);
        var js = Read(ViewerJs);
        var css = Read("ALDevToolbox/wwwroot/source-viewer.css");

        foreach (var cls in RetiredOutlineClasses)
        {
            markup.Should().NotContain(cls,
                because: $".{cls} belonged to the viewer #562 deleted");
            js.Should().NotContain(cls,
                because: "the client-side renderers feed the same panel as the markup");
            Selectors(css).Should().NotContain(sel => sel.Contains(cls),
                because: $"source-viewer.css loads after pages-power.css, so a returning .{cls} "
                       + "would out-specify the design layer without anything failing");
        }
    }

    [Fact]
    public void There_is_only_one_source_viewer()
    {
        var dir = Path.Combine(Root(), "ALDevToolbox", "Components", "Pages", "ObjectExplorer");
        Directory.EnumerateFiles(dir, "SourceFileViewer*.razor")
            .Select(Path.GetFileName)
            .Should().BeEquivalentTo(["SourceFileViewer.razor"],
                because: "#562 retired the rollback copy; two viewers meant two vocabularies "
                       + "for one page, and the spare had been untested for 55 releases");

        // The class comment still names the env var, on purpose - it is the
        // history of the route. What must be gone is any code that reads it.
        Read("ALDevToolbox/Services/ObjectExplorer/ObjectExplorerLinks.cs")
            .Should().NotContain("GetEnvironmentVariable",
                because: "the env var only ever chose between the two viewers");
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
            .Concat(["ALDevToolbox/wwwroot/app.css",
                     "ALDevToolbox/wwwroot/source-viewer.css", "ALDevToolbox/wwwroot/code-editor.css"])
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
