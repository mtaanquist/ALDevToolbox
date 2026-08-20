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
    /// The panel switch is class-driven. A panel whose tab always exists must
    /// therefore not also carry <c>hidden</c> — with the guard above in place
    /// the attribute wins and the panel renders blank, which is exactly what
    /// happened to the shortcuts panel.
    /// </summary>
    [Fact]
    public void The_always_available_panel_is_shown_by_its_class_alone()
    {
        var markup = Read(Viewer);
        var help = Regex.Match(markup, @"<div class=""source-viewer__panel""[^>]*data-panel=""help""[^>]*>");
        help.Success.Should().BeTrue(because: "the shortcuts panel is rendered server-side");
        help.Value.Should().NotContain(" hidden",
            because: "[hidden] is !important, so it would beat .source-viewer__panel.is-active");

        // The panels whose tab is conditional keep it: hidden is how the tab
        // controller says "this view does not exist yet".
        markup.Should().MatchRegex(@"data-panel=""find""[^>]*hidden",
            because: "the Find panel has no content until a search runs");
    }

    /// <summary>
    /// The outline row's leading glyph replaced a text kind badge, so the kind
    /// now only reaches the user through the tooltip. Every kind the badge
    /// vocabulary knows still needs a glyph, or the row leads with a fallback
    /// character that means nothing.
    /// </summary>
    [Fact]
    public void Every_kind_the_viewer_labels_also_has_a_glyph()
    {
        var markup = Read(Viewer);
        var labelled = KindsIn(markup, "KindBadgeLabel");
        var glyphed = KindsIn(markup, "KindGlyph");
        labelled.Should().NotBeEmpty();

        // Member kinds are the ones that reach an outline row's glyph column;
        // object kinds all share the object glyph via the switch default.
        var members = new[]
        {
            "table_field", "page_field", "page_action", "trigger", "label",
            "procedure", "internal_procedure", "protected_procedure", "local_procedure",
            "event_publisher", "event_subscriber",
        };
        foreach (var kind in members)
        {
            labelled.Should().Contain(kind, because: "it is part of the badge vocabulary");
            glyphed.Should().Contain(kind,
                because: $"{kind} rows would otherwise fall through to the object glyph");
        }
    }

    /// <summary>
    /// The client-side renderers cannot go through <c>&lt;Icon&gt;</c>, so three
    /// Lucide glyphs are inlined in <c>source-viewer.js</c>. That path skips
    /// <c>IconCatalog</c>'s build-time check, so pin the copies against the
    /// vendored files instead — a re-vendor at a new Lucide version would
    /// otherwise leave the JS drawing the old shape.
    /// </summary>
    [Theory]
    [InlineData("chevron-right", "CARET_ICON_SVG")]
    [InlineData("x", "CLOSE_ICON_SVG")]
    [InlineData("search", "SEARCH_ICON_SVG")]
    public void Inlined_icons_match_the_vendored_svg(string icon, string constant)
    {
        var svg = Read($"ALDevToolbox/Resources/Icons/{icon}.svg");
        var js = Read(ViewerJs);
        var start = js.IndexOf($"const {constant} =", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, because: $"{constant} is declared in the viewer");
        var block = js[start..js.IndexOf(";\n", start, StringComparison.Ordinal)];

        var shapes = Regex.Matches(svg, @"<(path|circle)\b[^>]*>")
            .Select(m => Regex.Matches(m.Value, @"(d|cx|cy|r)=""([^""]+)""")
                .Select(a => $"{a.Groups[1].Value}=\"{a.Groups[2].Value}\"")
                .ToList())
            .ToList();
        shapes.Should().NotBeEmpty();
        foreach (var attrs in shapes)
        {
            foreach (var attr in attrs)
            {
                block.Should().Contain(attr,
                    because: $"{constant} has to draw the same shape as {icon}.svg");
            }
        }
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
