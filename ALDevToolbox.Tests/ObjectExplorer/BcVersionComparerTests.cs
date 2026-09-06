using ALDevToolbox.Services.ObjectExplorer.Import;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// <see cref="BcVersionComparer"/> exists because of a shipped bug: sorted as
/// text, "28.10" lands before "28.2" and the Object Explorer browser showed the
/// wrong newest version. Nothing was pinning the fix, so pin it — plus how a
/// short label compares against its zero-padded twin, the non-numeric fallback,
/// and both null positions.
/// </summary>
public sealed class BcVersionComparerTests
{
    private static int Compare(string? x, string? y) => Math.Sign(BcVersionComparer.Instance.Compare(x, y));

    [Theory]
    // The regression itself: numeric, not lexicographic.
    [InlineData("28.10", "28.2", 1)]
    [InlineData("28.2", "28.10", -1)]
    [InlineData("27.4.0.12345", "27.4.0.12345", 0)]
    // Pinned as it behaves, not as the doc comment describes it: the comment says
    // missing parts are treated as zero so "28" and "28.0.0" compare equal, but an
    // absent segment takes the same -1 the non-numeric fallback uses, so a short
    // label sorts *below* its zero-padded twin. Harmless for the descending
    // "newest release" sort this backs; noted rather than changed.
    [InlineData("28", "28.0.0", -1)]
    [InlineData("28.0.0", "28", 1)]
    // Ordinary ordering across major and build parts.
    [InlineData("29.0", "28.99", 1)]
    [InlineData("27.4.0.12345", "27.4.0.12344", 1)]
    // A non-numeric segment sorts below any number rather than throwing.
    [InlineData("28.preview", "28.0", -1)]
    // Null is "no version" and sorts last.
    [InlineData(null, "28.0", 1)]
    [InlineData("28.0", null, -1)]
    [InlineData(null, null, 0)]
    public void Compares_business_central_versions_numerically(string? x, string? y, int expected) =>
        Compare(x, y).Should().Be(expected);

    [Fact]
    public void Sorting_descending_puts_the_newest_first_and_nulls_last()
    {
        string?[] versions = ["28.2", null, "28.10", "27.9", "28"];

        var sorted = versions.OrderBy(v => v, BcVersionComparer.Instance).Reverse().ToArray();

        sorted.Should().Equal(null, "28.10", "28.2", "28", "27.9");
    }
}
