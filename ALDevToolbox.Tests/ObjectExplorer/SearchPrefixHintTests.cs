using ALDevToolbox.Components.Pages.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The search-tips panel (#575) teaches one rule rather than a list of
/// seventeen: <b>the two-letter badge in the Type column, lower-cased, is the
/// search prefix.</b> That is true today by coincidence of two independent
/// tables -- <c>ObjectKindGlyph.For</c> and <c>ObjectSearchRanking</c>'s
/// prefix map -- and nothing but this test keeps it true.
///
/// If it ever stops being true, the panel is lying to the user, and the fix is
/// either to realign the tables or to stop teaching the rule.
/// </summary>
public sealed class SearchPrefixHintTests
{
    /// <summary>Every object kind the Type column draws a badge for.</summary>
    public static TheoryData<string> Kinds()
    {
        var data = new TheoryData<string>();
        foreach (var kind in new[]
                 {
                     "table", "page", "codeunit", "report", "query", "xmlport", "enum",
                     "interface", "permissionset", "controladdin", "tableextension",
                     "pageextension", "reportextension", "enumextension",
                     "permissionsetextension", "menusuite", "profile",
                 })
        {
            data.Add(kind);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void The_badge_lower_cased_is_a_search_prefix(string kind)
    {
        var badge = ObjectKindGlyph.For(kind);
        badge.Should().NotBeEmpty(because: $"the Type column draws a badge for {kind}");

        ObjectSearchRanking.IsKindPrefix(badge.ToLowerInvariant()).Should().BeTrue(
            because: $"the tips panel tells the reader that \"{badge}\" in the Type column " +
                     $"means they can search \"{badge.ToLowerInvariant()}:\"");
    }

    /// <summary>
    /// And the prefix has to select the kind whose badge it is -- a prefix that
    /// resolves to a different kind would be worse than one that does not
    /// resolve at all, because the search would quietly return the wrong rows.
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void The_prefix_scopes_to_the_kind_whose_badge_it_is(string kind)
    {
        var badge = ObjectKindGlyph.For(kind).ToLowerInvariant();
        var (resolved, remainder) = ObjectSearchRanking.ExtractKindPrefix($"{badge}:sales");

        resolved.Should().Be(kind, because: $"\"{badge}:\" is the badge for {kind}");
        remainder.Should().Be("sales", because: "the prefix is consumed, the search term is not");
    }
}
