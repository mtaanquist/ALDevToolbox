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
}
