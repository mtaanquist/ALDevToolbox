using FluentAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the two width decisions in the content archetype (divergences 72 and
/// 73), both raised by the maintainer looking at rendered pages.
///
/// <para>The pair is worth reading together, because they pull opposite ways and
/// the reason is the same one. A reading measure belongs to <b>prose</b>. On
/// <c>/tools/mcp</c> the measure had been put on the step <i>list</i>, which also
/// holds a two-up card row, a token field and JSON config - so the container was
/// held to 82ch and the UI inside it was crushed. On <c>/docs/mcp</c> the column
/// really is all prose, so the measure is right and it was the grid around it
/// that was wrong.</para>
/// </summary>
public sealed class DocsLayoutTests
{
    private const string Sheet = "ALDevToolbox/wwwroot/pages-content.css";

    /// <summary>
    /// Divergence 73. The handoff's <c>1fr 216px</c> lets the first track absorb
    /// every spare pixel while the article stays at its measure, so the contents
    /// list is pinned to the far right with a hole in between: 658px of nothing
    /// at 1760px, wider than the article itself. Sizing the track to the measure
    /// puts the contents beside the text and turns the surplus into one right
    /// margin.
    /// </summary>
    [Fact]
    public void The_contents_column_sits_beside_the_article_not_across_a_gap()
    {
        var sheet = Read(Sheet);

        sheet.Should().Contain("grid-template-columns: minmax(0, 74ch) 216px",
            "a 1fr first track stretches to the full page and strands the contents "
            + "list on the far side of a gap wider than the article");
        sheet.Should().Contain("justify-content: start",
            "without it the sized tracks are spread across the container and the gap "
            + "comes straight back");
    }

    /// <summary>
    /// The measure itself, which is NOT the thing that was wrong and is the thing
    /// most likely to be "fixed" by someone closing the gap from the other end.
    /// Verified in the browser: a body paragraph is 14px and exactly 74
    /// characters fit the 558px line.
    /// </summary>
    [Fact]
    public void The_article_keeps_its_reading_measure()
    {
        Read(Sheet).Should().Contain(".docs__main { min-width: 0; max-width: 74ch;",
            "widening the prose to reach the contents list would trade a layout "
            + "wrinkle for an actual readability regression");
    }

    /// <summary>
    /// The silent one. Both rules select <c>.docs__toc</c>, so they tie on
    /// specificity and source order decides. The handoff groups its
    /// <c>display: none</c> with the grid change at the top of the section, where
    /// the component's own <c>display: grid</c> - declared later - beats it. The
    /// contents list had therefore never hidden on collapse: it rendered as a
    /// full-width block under the article at every narrow width since the port.
    ///
    /// <para>Ordering is exactly the kind of thing a later tidy-up "restores", so
    /// it is pinned by position rather than by presence.</para>
    /// </summary>
    [Fact]
    public void The_contents_list_actually_hides_when_the_layout_collapses()
    {
        var sheet = Read(Sheet);

        var declaresGrid = sheet.IndexOf(".docs__toc { position: sticky;", StringComparison.Ordinal);
        var hides = sheet.IndexOf("@container (max-width: 1000px) { .docs__toc { display: none; } }", StringComparison.Ordinal);

        declaresGrid.Should().BeGreaterThan(-1);
        hides.Should().BeGreaterThan(-1, "the collapse has to hide the contents list");
        hides.Should().BeGreaterThan(declaresGrid,
            "both selectors are '.docs__toc', so they tie on specificity and the later "
            + "one wins - grouped with the grid change at the top of the section, "
            + "display: none loses to the component's own display: grid and the rule "
            + "does nothing at all");
    }

    /// <summary>
    /// Divergence 72, the opposite direction: the step list opts out of the
    /// measure and the measure moves to <c>.step__text</c>, the only part of a
    /// step that is prose.
    /// </summary>
    [Fact]
    public void A_step_list_can_opt_out_of_the_measure_but_its_prose_cannot()
    {
        var sheet = Read(Sheet);

        sheet.Should().Contain(".steps--wide { max-width: none; }",
            "a step list whose bodies are cards, inputs and code is not a column of prose");
        sheet.Should().Contain(".step__text { font-size: var(--text-sm); color: var(--ink-3); text-wrap: pretty; max-width: 82ch; }",
            "the measure has to land on the step's prose, or opting out of it loses "
            + "readability along with the cap");

        Read("ALDevToolbox/Components/Pages/Mcp.razor").Should().Contain("steps steps--wide",
            "the MCP page is the case that raised it - two-up cards crushed to 265px "
            + "each and a server address wrapped mid-token");
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
