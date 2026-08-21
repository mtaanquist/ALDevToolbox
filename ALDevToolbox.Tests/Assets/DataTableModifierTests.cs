using System.Text.RegularExpressions;
using FluentAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Pins the four <c>.data-table</c> column modifiers against the rule that had
/// been silently beating all of them.
///
/// <c>.data-table th, .data-table td</c> sets <c>text-align: left</c> and weighs
/// (0,1,1). A bare <c>.data-table__actions</c> weighs (0,1,0) and loses, so the
/// alignment those modifiers exist to apply never took effect — on any page,
/// including the reference ones. It is invisible while the cell's content
/// happens to fill it: the Object Explorer's actions cell only looked right
/// until a design review swapped its 200px-wide split button for a 26px kebab,
/// which then sat at the left edge with 162px of dead space beside it.
///
/// It had been found once before and patched for one modifier
/// (<c>.page .data-table .data-table__num</c> in base.css), which left the
/// other three broken and the cause recorded in a place the design layer could
/// not see. The fix belongs upstream: scoping each modifier under
/// <c>.data-table</c> makes it (0,2,0), and two classes beat one class plus an
/// element whatever the element is.
///
/// This is a specificity contract, not a spelling one — a future edit that
/// re-flattens a selector, or adds a new <c>.data-table td</c> rule at higher
/// weight, puts the alignment back to silently wrong.
/// </summary>
public sealed class DataTableModifierTests
{
    private const string Components = "ALDevToolbox/wwwroot/components.css";

    /// <summary>The modifier, and the property it exists to set.</summary>
    public static TheoryData<string, string> Modifiers => new()
    {
        { "data-table__num", "text-align" },
        { "data-table__actions", "text-align" },
        { "data-table__col-state", "text-align" },
        { "data-table__col-check", "text-align" },
    };

    [Theory]
    [MemberData(nameof(Modifiers))]
    public void A_column_modifier_outranks_the_cell_rule_it_has_to_beat(string modifier, string property)
    {
        var css = Read(Components);

        var cellRule = Rules(css).FirstOrDefault(r =>
            r.Selector.Contains(".data-table th") && r.Selector.Contains(".data-table td"));
        cellRule.Selector.Should().NotBeNull(
            "the whole point of this test is the rule the modifiers compete with");
        cellRule.Body.Should().Contain(property,
            because: $"if the cell rule stops setting {property}, these modifiers no longer need "
                   + "the extra weight and this test should be deleted rather than worked around");

        var winner = cellRule.Selector.Split(',').Max(s => Specificity(s));

        var modifierRules = Rules(css)
            .Where(r => r.Selector.Split(',').Any(s => s.Contains($".{modifier}")))
            .Where(r => r.Body.Contains(property))
            .ToList();

        modifierRules.Should().NotBeEmpty(because: $".{modifier} has to set {property} somewhere");

        modifierRules.Should().Contain(
            r => r.Selector.Split(',')
                  .Where(s => s.Contains($".{modifier}"))
                  .Any(s => Specificity(s) > winner),
            because: $".{modifier} is (0,1,0) on its own and `.data-table td` is (0,1,1), so the "
                   + $"bare modifier loses and its {property} never applies — with nothing failing, "
                   + "because the cell still renders and the content is often wide enough to hide it");
    }

    [Fact]
    public void No_sheet_re_bridges_the_alignment_the_design_layer_now_answers()
    {
        foreach (var sheet in new[] { "ALDevToolbox/wwwroot/base.css", "ALDevToolbox/wwwroot/tools.css",
                                     "ALDevToolbox/wwwroot/source-viewer.css",
                                      "ALDevToolbox/wwwroot/admin.css" })
        {
            Rules(Read(sheet))
                .Where(r => r.Body.Contains("text-align"))
                .Should().NotContain(r => r.Selector.Contains("data-table__num")
                                       || r.Selector.Contains("data-table__actions"),
                    because: $"{sheet} carried a `.page .data-table .data-table__num` patch for "
                           + "exactly this bug. The design layer answers it now, and a second "
                           + "copy in the legacy layer is how the other three modifiers stayed "
                           + "broken for months — the workaround recorded the fix somewhere the "
                           + "sheet that owned the problem could not see it");
        }
    }

    /// <summary>
    /// CSS specificity as a comparable number: ids, then classes /attributes /
    /// pseudo-classes, then elements / pseudo-elements. Enough for the flat
    /// selectors in these sheets — no :is()/:where() weighting.
    /// </summary>
    private static int Specificity(string selector)
    {
        var s = selector.Trim();
        var ids = Regex.Matches(s, @"#[\w-]+").Count;
        var classes = Regex.Matches(s, @"\.[\w-]+").Count
                    + Regex.Matches(s, @"\[[^\]]+\]").Count
                    + Regex.Matches(s, @"(?<!:):(?!:)[\w-]+").Count;
        var elements = Regex.Matches(s, @"(^|[\s>+~])([a-zA-Z][\w-]*)").Count;
        return (ids * 10_000) + (classes * 100) + elements;
    }

    private static IEnumerable<(string Selector, string Body)> Rules(string css)
    {
        var stripped = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        foreach (Match m in Regex.Matches(stripped, @"(?<sel>[^{}@]+)\{(?<body>[^{}]*)\}"))
        {
            yield return (m.Groups["sel"].Value.Trim(), m.Groups["body"].Value);
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
