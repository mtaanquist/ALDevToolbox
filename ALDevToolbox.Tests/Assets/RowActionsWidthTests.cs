using System.Text.RegularExpressions;
using FluentAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the fix for #574, which was not the layout bug it looked like.
///
/// <para>Four admin lists were clipping the right-hand end of the page below
/// 1100px. The cause was not the page frame and not the filter bar: it was the
/// <c>.data-table__actions</c> cell, still rendering the pre-redesign row of
/// full text buttons ("Set default", "Edit", "Duplicate" — 310px of a 938px
/// table). A table's min-content opens the whole content column, every
/// stretched sibling then reports the same over-wide box, and
/// <c>.app__content</c> is <c>overflow-x: hidden</c> — so the excess was
/// <b>clipped rather than scrollable</b>. At 1100px that cost
/// <c>/admin/cookbook</c> five unreachable Delete buttons; at 880px, its own
/// New recipe button.</para>
///
/// <para>The design system's list archetype puts exactly one control in that
/// cell — a <c>.ra</c> kebab (see <c>PageList.dc.html</c>, whose menu reads
/// "Use template / Duplicate / Edit metadata / Archive"). <c>/pipelines</c> had
/// already been ported to it and overflows at no width at all. So this is a
/// fidelity gap, not a responsive one, and the fix was to finish the port
/// rather than to wrap the tables in a scroll container — which would have
/// re-introduced the clipping that divergence 6 removed <c>overflow: hidden</c>
/// from <c>.data-table</c> to avoid.</para>
///
/// <para>Text is the right medium for this check. The failure is invisible in
/// markup review (a row of buttons looks fine) and invisible in a screenshot at
/// a desktop width, which is why it survived the port.</para>
/// </summary>
public sealed class RowActionsWidthTests
{
    /// <summary>
    /// Pages whose row-actions cells were converted, plus the one that was
    /// already right. Named explicitly rather than swept: several other tables
    /// still carry a single small link in that cell, which costs nothing and is
    /// not what this issue was about.
    /// </summary>
    public static TheoryData<string> ConvertedPages => new()
    {
        "Components/Pages/Admin/AdminTemplateList.razor",
        "Components/Pages/Admin/AdminModuleList.razor",
        "Components/Pages/Admin/AdminCookbook.razor",
        "Components/Pages/SiteAdmin/SiteAdminUsers.razor",
        "Components/Pages/Pipelines/PipelinesBrowser.razor",
    };

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void A_converted_row_actions_cell_holds_a_kebab_and_no_text_buttons(string page)
    {
        foreach (var (cell, line) in ActionCells(Read("ALDevToolbox/" + page)))
        {
            // A cell with a single small link is fine and common; what must not
            // come back is a *row* of labelled controls, which is what opened
            // the column.
            var controls = Regex.Matches(cell, @"<(?:button|a)\b").Count;
            if (controls <= 1) continue;

            // Either the shared component or the inline `.ra` markup - the
            // Pipelines browser predates RowActions and writes its own, which
            // is the same component as far as this rule is concerned.
            var kebab = cell.Contains("<RowActions") || cell.Contains("class=\"ra\"");
            kebab.Should().BeTrue(
                $"{page}:{line} has {controls} controls in one .data-table__actions cell; "
                + "the list archetype puts them in a .ra kebab (see #574)");
            cell.Should().NotContain("btn btn--sm",
                $"{page}:{line} still renders a text button beside the kebab");
        }
    }

    /// <summary>
    /// Folding Edit into the kebab is only acceptable because the row itself
    /// became the way in. Without this the most common action on each of these
    /// pages would have gone from one click to two — a real cost, paid to fix a
    /// layout bug, which is the kind of trade nobody notices until they use the
    /// page every day.
    /// </summary>
    [Theory]
    [InlineData("Components/Pages/Admin/AdminTemplateList.razor", "/admin/templates/")]
    [InlineData("Components/Pages/Admin/AdminModuleList.razor", "/admin/modules/")]
    [InlineData("Components/Pages/Admin/AdminCookbook.razor", "/admin/cookbook/")]
    public void The_row_keeps_a_one_click_way_into_its_detail_page(string page, string hrefStem)
    {
        var body = Read("ALDevToolbox/" + page);

        // An anchor to the detail page that is NOT a menu entry and NOT a
        // button - i.e. one the reader can hit without opening anything first.
        var plainLink = Regex.Matches(body, "<a\\b[^>]*>")
            .Select(m => m.Value)
            .Where(tag => tag.Contains(hrefStem, StringComparison.Ordinal))
            .Any(tag => !tag.Contains("menu__item", StringComparison.Ordinal)
                        && !tag.Contains("btn", StringComparison.Ordinal));

        plainLink.Should().BeTrue(
            $"{page} moved Edit into the kebab, so the row itself has to link to {hrefStem} "
            + "- otherwise the page's most common action quietly became two clicks");
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

    private static IEnumerable<(string Cell, int Line)> ActionCells(string source)
    {
        foreach (Match m in Regex.Matches(
                     source, @"<td[^>]*class=""[^""]*data-table__actions[^""]*""[^>]*>.*?</td>",
                     RegexOptions.Singleline))
        {
            yield return (m.Value, source[..m.Index].Count(c => c == '\n') + 1);
        }
    }
}
