using AwesomeAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards divergence 70: the detail archetype's optional rail.
///
/// <para><c>PageDetail.dc.html</c> is single-column, and PR 15d dissolved this
/// page's pre-redesign rail into the <c>.meta-row</c> on that authority. For the
/// <i>facts</i> that was an improvement. What it could not absorb were the two
/// things that are not facts - the repositories a build was cut from, and the
/// compare control - and with no rail they fell to the bottom of the main
/// column, under the one section that has no upper bound on its height. Measured
/// on <c>/pipelines/5</c> with a short log and no compare card at all,
/// "Repositories" began 1052px down a 1000px window; with the rail it is at
/// 288px.</para>
///
/// <para>Two things about the rail are easy to undo by tidying, so both are
/// pinned here rather than left to review.</para>
/// </summary>
public sealed class DetailRailTests
{
    private const string Page = "ALDevToolbox/Components/Pages/Pipelines/PipelineBuilds.razor";

    /// <summary>
    /// The rail is the point: the two supporting sections have to render inside
    /// the aside, not after it. A re-indent that moved either back into
    /// <c>.detail-body__main</c> would look harmless in the diff and would put
    /// them back under the build log.
    /// </summary>
    [Theory]
    [InlineData("Repositories")]
    [InlineData("Compare builds")]
    public void The_supporting_sections_render_in_the_rail(string heading)
    {
        var body = Read(Page);

        var asideOpen = body.IndexOf("<aside class=\"detail-body__aside\"", StringComparison.Ordinal);
        var asideClose = body.IndexOf("</aside>", StringComparison.Ordinal);
        var section = body.IndexOf($">{heading}</h2>", StringComparison.Ordinal);

        asideOpen.Should().BeGreaterThan(-1, "the detail rail is what puts these above the fold");
        asideClose.Should().BeGreaterThan(asideOpen);
        section.Should().BeInRange(asideOpen, asideClose,
            $"'{heading}' is supporting reference, not a fact; outside the rail it lands "
            + "under the build log, which is unbounded in height");
    }

    /// <summary>
    /// The half that fails silently. Whether the rail has anything in it is a
    /// per-record question - a pipeline whose only build failed before it
    /// recorded a commit has neither repositories nor a second build to compare
    /// - and an aside that renders empty still reserves its 280px, so the
    /// content stops short of the page edge for no reason a reader could see.
    /// That is the bug <c>.settings__body--wide</c> already exists to prevent.
    /// </summary>
    [Fact]
    public void An_empty_rail_is_not_reserved()
    {
        var body = Read(Page);

        body.Should().Contain("detail-body--wide",
            "a detail page with nothing to put beside its content must give the "
            + "column back rather than render an empty 280px aside");

        var guard = body.IndexOf("@if (HasRail)", StringComparison.Ordinal);
        var asideOpen = body.IndexOf("<aside class=\"detail-body__aside\"", StringComparison.Ordinal);

        guard.Should().BeGreaterThan(-1, "the aside element itself has to be gated, not just its contents");
        asideOpen.Should().BeGreaterThan(guard,
            "gating only the sections inside the aside still emits an empty rail");
    }

    /// <summary>
    /// The rail's width is only safe because of what sits beside it.
    /// <c>.app__content</c> is <c>overflow-x: hidden</c>, so a table whose
    /// min-content exceeds its column is CLIPPED rather than scrollable (#574).
    /// Measured on this page: the build history's min-content is 670px against a
    /// 844px main column, and 780px at the narrowest point before the 1080px
    /// collapse. Widening the rail, or dropping the collapse, spends that
    /// headroom - so the sheet has to keep saying both numbers.
    /// </summary>
    [Fact]
    public void The_archetype_keeps_its_collapse_and_its_reasoning()
    {
        var sheet = Read("ALDevToolbox/wwwroot/pages.css");

        sheet.Should().Contain(".detail-body {", "the archetype has to exist to be used");
        sheet.Should().Contain("@container (max-width: 1080px) { .detail-body",
            "the rail collapses at the same width as the two asides already in the system");
        sheet.Should().Contain("670",
            "the table min-content the rail's width is safe against is the number a "
            + "future widening has to re-measure, so it stays written down");
    }

    /// <summary>
    /// The regression the maintainer caught by looking at the finished page:
    /// four sections carrying three different heading treatments - a
    /// <c>.card__title</c> in a bordered head, a bare <c>.pb-sec__title</c>, and
    /// a mono uppercase <c>.state-label</c>. Each was individually defensible
    /// (a bordered list inside a padded card really is a box in a box, which is
    /// why two of them skipped the card) and together they read as unconsidered.
    ///
    /// <para>Nothing here says a section may not be bare. It says this page must
    /// not mix the two, which is the property a reader actually sees and the one
    /// that quietly decays as sections are added.</para>
    /// </summary>
    [Fact]
    public void Every_section_on_the_page_is_titled_the_same_way()
    {
        // Razor comments are stripped first: this file explains the two
        // retired treatments by name, and a check that cannot tell prose from
        // markup fails on its own rationale. (It did.)
        var body = WithoutComments(Read(Page));

        foreach (var orphan in new[] { "pb-sec__title", "state-label" })
        {
            body.Should().NotContain(orphan,
                $"'{orphan}' is a second way to title a section on a page whose "
                + "other headings are .card__title; pick one");
        }

        // And the flush modifier is what lets a self-framed container live in a
        // card without drawing a second border, so the two conversions depend
        // on it rather than on a padded .card__body.
        body.Should().Contain("card card--flush",
            "a .data-table or .sub-rows inside a padded .card__body is the box-in-a-box "
            + "this page originally avoided by skipping the card entirely");
    }

    /// <summary>Strips Razor <c>@* ... *@</c> comments so a class-name check reads markup, not prose.</summary>
    private static string WithoutComments(string razor) =>
        System.Text.RegularExpressions.Regex.Replace(razor, @"@\*.*?\*@", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);

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
