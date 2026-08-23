using System.Text.RegularExpressions;
using FluentAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the detail-page head, which PR 15c moved off the private
/// <c>.det-*</c> / <c>.art-*</c> / <c>.rel-empty*</c> dialect onto the design
/// system's <c>.page-head</c> and <c>.empty-state</c>.
///
/// The dialect was shared by three pages at once — <c>PipelineBuilds</c>,
/// <c>ProjectDetail</c> and <c>ReleasePipelineDetail</c> — which is why they had
/// to move together: porting one would have left the rules alive for the other
/// two and the migration would have looked done while nothing was deleted.
///
/// Two ways this breaks quietly:
///
/// <b>A returning rule out-specifies the design layer.</b> <c>tools.css</c>
/// loads after <c>components.css</c>, so re-adding any of these names does not
/// error — it silently wins, and the page drifts back one property at a time.
///
/// <b>A call site keeps a name whose rules are gone.</b> The class still parses,
/// the element still renders, and the head loses its layout without anything
/// failing. That is what a stale <c>.det-sub</c> would do: an unstyled div where
/// a meta line used to be.
///
/// The <c>.empty-state__icon</c> check is a shape rule, not a spelling one. It
/// is a 42px grid box that centres a glyph, so the class belongs on a wrapping
/// element. The dialect it replaced worked the other way round — <c>.rel-empty-ico</c>
/// went on the <c>&lt;Icon&gt;</c> itself via <c>Css=</c> — so the mechanical
/// translation of that markup produces a sized grid container with nothing to
/// centre and a glyph stretched to 42px.
/// </summary>
public sealed class DetailHeadTests
{
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

    /// <summary>
    /// The head dialect PR 15c deleted. <c>.det-grid</c> / <c>.det-col</c> /
    /// <c>.det-card</c> are deliberately absent: they are the body layout, and
    /// they retire with PRs 15d and 15e.
    /// </summary>
    private static readonly string[] Retired =
    [
        "det-bc", "det-head", "det-id", "det-pico", "det-title", "det-sub", "det-actions",
        "art-page", "art-detail", "art-fail",
        "rel-empty", "rel-empty-ico", "rel-empty-h", "rel-empty-p",
        "dotsep",
    ];

    /// <summary>The three pages that shared the dialect and had to move together.</summary>
    public static TheoryData<string> DetailPages => new()
    {
        "ALDevToolbox/Components/Pages/Pipelines/PipelineBuilds.razor",
        "ALDevToolbox/Components/Pages/Projects/ProjectDetail.razor",
        "ALDevToolbox/Components/Pages/Pipelines/ReleasePipelineDetail.razor",
    };

    [Fact]
    public void The_legacy_detail_head_dialect_is_gone_from_every_sheet()
    {
        foreach (var cls in Retired)
        {
            foreach (var sheet in Sheets)
            {
                Selectors(Read(sheet)).Should().NotContain(sel => Regex.IsMatch(sel, $@"\.{cls}\b"),
                    because: $".{cls} was the private head dialect PR 15c retired; the later sheets load "
                           + "after the design layer, so a returning rule would out-specify "
                           + ".page-head rather than conflict with it");
            }
        }
    }

    [Fact]
    public void No_component_still_renders_the_legacy_detail_head()
    {
        foreach (var file in Razors())
        {
            var classes = RenderedClasses(StripComments(File.ReadAllText(file))).ToHashSet();
            classes.Overlaps(Retired).Should().BeFalse(
                because: $"{Relative(file)} names a class with no rules left — the element still "
                       + "renders, so the head simply loses its layout with nothing failing");
        }
    }

    [Theory]
    [MemberData(nameof(DetailPages))]
    public void Each_detail_page_heads_with_crumbs_and_a_title(string page)
    {
        var markup = StripComments(Read(page));
        var classes = RenderedClasses(markup).ToHashSet();

        classes.Should().Contain("page", because: $"{page} is a page archetype body");
        classes.Should().Contain("detail-head",
            because: "PageDetail.dc.html is the archetype for these three, and it is not "
                   + ".page-head - the detail head carries a title ROW so a state pill can sit "
                   + "beside the title, which .page-head has nowhere to put");
        classes.Should().Contain("detail-head__title-row");
        classes.Should().Contain("detail-head__title");
        classes.Should().Contain("page-head__crumbs",
            because: "a detail page is reached from a list, and the crumb row is the way back");
    }

    [Theory]
    [MemberData(nameof(DetailPages))]
    public void The_crumb_row_sits_outside_the_detail_head(string page)
    {
        var markup = StripComments(Read(page));

        var crumbs = markup.IndexOf("page-head__crumbs", StringComparison.Ordinal);
        var head = markup.IndexOf("class=\"detail-head\"", StringComparison.Ordinal);

        crumbs.Should().BeGreaterThan(-1);
        head.Should().BeGreaterThan(-1);
        crumbs.Should().BeLessThan(head,
            because: "the two archetypes differ and it is easy to copy the wrong one: "
                   + "PageList.dc.html nests the crumbs INSIDE .page-head, PageDetail.dc.html "
                   + "puts them above .detail-head as a sibling. Nesting them here pulls the "
                   + "crumbs into the flex row that holds the title and the actions");
    }

    [Fact]
    public void No_detail_page_carries_two_pills_for_one_state()
    {
        var markup = StripComments(Read("ALDevToolbox/Components/Pages/Pipelines/PipelineBuilds.razor"));

        // `class="status-pill status-pill--x"`, not the `__dot` child inside it.
        Regex.Matches(markup, @"class=""status-pill[ ""]").Count.Should().Be(1,
            because: "the build's state belongs beside the page title, where the archetype "
                   + "puts it. The Latest-build card had a second pill saying the same word, "
                   + "which reads as two different facts until you look twice");
    }

    [Fact]
    public void An_empty_state_glyph_is_wrapped_rather_than_worn_by_the_icon()
    {
        foreach (var file in Razors())
        {
            var markup = StripComments(File.ReadAllText(file));

            Regex.IsMatch(markup, @"<Icon[^>]*Css=""[^""]*\bempty-state__icon\b").Should().BeFalse(
                because: $"{Relative(file)} would put the 42px tinted grid box on the <svg> itself, "
                       + "stretching the glyph instead of centring it in a tile — the shape the "
                       + "retired .rel-empty-ico had, which is exactly what a mechanical port "
                       + "of that markup reproduces");
        }
    }

    // ── Helpers (mirroring RowActionsMenuTests) ────────────────────────

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

    private static IEnumerable<string> Selectors(string css)
    {
        var stripped = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        foreach (Match m in Regex.Matches(stripped, @"(?<sel>[^{}@]+)\{[^{}]*\}"))
        {
            yield return m.Groups["sel"].Value.Trim();
        }
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
