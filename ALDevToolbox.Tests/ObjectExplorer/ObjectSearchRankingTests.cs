using ALDevToolbox.Services.ObjectExplorer;
using FluentAssertions;
using Xunit;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Pure-parsing tests for the search-box syntax (no database). The matching
/// itself runs through EF and is covered by the integration tests in
/// <see cref="ObjectExplorerServiceTests"/>; these lock down the classification
/// that decides which matching mode a token takes.
/// </summary>
public class ObjectSearchRankingTests
{
    [Theory]
    [InlineData("50000..99999", true, 50000, 99999)]
    [InlineData("18..18", true, 18, 18)]            // single-value range
    [InlineData("99999..50000", true, 50000, 99999)] // reversed bounds tolerated
    [InlineData("18", false, 0, 0)]                  // plain number, not a range
    [InlineData("..99999", false, 0, 0)]             // open-ended stays a plain token
    [InlineData("50000..", false, 0, 0)]
    [InlineData("a..b", false, 0, 0)]                // non-numeric bounds
    [InlineData("1..2..3", false, 0, 0)]             // more than one ".."
    public void TryParseIdRange_classifies_range_tokens(string text, bool ok, int lo, int hi)
    {
        ObjectSearchRanking.TryParseIdRange(text, out var l, out var h).Should().Be(ok);
        if (ok)
        {
            l.Should().Be(lo);
            h.Should().Be(hi);
        }
    }

    // Kind passed as a string so this public xUnit method doesn't expose the
    // internal GlobKind enum in its signature (CS0051).
    [Theory]
    [InlineData("sales*", "Prefix", "sales")]
    [InlineData("*header", "Suffix", "header")]
    [InlineData("*sales*", "Contains", "sales")]
    [InlineData("sa*es", "Contains", "saes")] // internal '*' → contains
    [InlineData("sales", "None", "sales")]    // no '*' → substring default
    public void ParseGlob_classifies_wildcard_tokens(string text, string kind, string needle)
    {
        var (k, n) = ObjectSearchRanking.ParseGlob(text);
        k.ToString().Should().Be(kind);
        n.Should().Be(needle);
    }

    [Theory]
    [InlineData("t:item", "table", "item")]
    [InlineData("table:item", "table", "item")]      // full name
    [InlineData("p:cust", "page", "cust")]
    [InlineData("c:post", "codeunit", "post")]
    [InlineData("te:item", "tableextension", "item")] // two-letter shortcut
    [InlineData("pse:foo", "permissionsetextension", "foo")] // three-letter shortcut
    [InlineData("T:Item", "table", "item")]          // case-insensitive prefix, remainder lowered
    [InlineData("t:", "table", "")]                   // bare prefix → kind only
    [InlineData("t:sales header", "table", "sales header")] // remainder keeps trailing tokens
    [InlineData("t:sales p:invoice", "table", "sales p:invoice")] // only first token consumed
    public void ExtractKindPrefix_splits_recognised_prefix(string search, string kind, string remainder)
    {
        var (k, rest) = ObjectSearchRanking.ExtractKindPrefix(search);
        k.Should().Be(kind);
        // ApplySearchTokens lower-cases tokens anyway; compare case-insensitively.
        rest.Should().BeEquivalentTo(remainder);
    }

    [Theory]
    [InlineData("item")]              // no prefix
    [InlineData("foo:bar")]           // unknown prefix → literal
    [InlineData("\"t:foo\"")]         // quoted first token left literal
    [InlineData("-t:foo")]            // negated first token left literal
    [InlineData(":item")]            // empty prefix
    [InlineData("")]                  // empty search
    public void ExtractKindPrefix_leaves_non_prefix_searches_untouched(string search)
    {
        var (k, rest) = ObjectSearchRanking.ExtractKindPrefix(search);
        k.Should().BeNull();
        rest.Should().Be(search);
    }
}
