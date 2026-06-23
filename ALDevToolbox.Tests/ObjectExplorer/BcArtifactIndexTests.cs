using ALDevToolbox.Services.ObjectExplorer;
using FluentAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Pins the pure artifact-index logic ported from BcContainerHelper's
/// <c>Get-BCArtifactUrl</c> — version parsing/selection, the blob→CDN URL shape,
/// the release-label format, and the manifest platformUrl read. No HTTP / DB, so
/// these run anywhere; <see cref="BcArtifactService"/> layers the network on top.
/// </summary>
public sealed class BcArtifactIndexTests
{
    // A trimmed country index in Microsoft's real shape: an array of records
    // carrying Version + CreationTime.
    private const string CountryJson = """
    [
      { "Version": "28.2.50931.51727", "CreationTime": "2026-06-10T00:00:00Z" },
      { "Version": "28.1.49000.50000", "CreationTime": "2026-05-10T00:00:00Z" },
      { "Version": "27.5.40000.41000", "CreationTime": "2026-04-10T00:00:00Z" },
      { "Version": "28.2.50000.50100", "CreationTime": "2026-06-01T00:00:00Z" }
    ]
    """;

    private const string PlatformJson = """
    [
      { "Version": "28.2.50931.51727", "CreationTime": "2026-06-10T00:00:00Z" },
      { "Version": "28.1.49000.50000", "CreationTime": "2026-05-10T00:00:00Z" },
      { "Version": "28.2.50000.50100", "CreationTime": "2026-06-01T00:00:00Z" }
    ]
    """;

    [Fact]
    public void ParseVersions_orders_newest_first()
    {
        var versions = BcArtifactIndex.ParseVersions(CountryJson, platformJson: null);

        versions.Should().ContainInOrder(
            "28.2.50931.51727", "28.2.50000.50100", "28.1.49000.50000", "27.5.40000.41000");
    }

    [Fact]
    public void ParseVersions_drops_versions_without_a_platform_artifact()
    {
        var versions = BcArtifactIndex.ParseVersions(CountryJson, PlatformJson);

        // 27.5.* has no platform entry, so it's filtered out.
        versions.Should().NotContain("27.5.40000.41000");
        versions.Should().ContainInOrder(
            "28.2.50931.51727", "28.2.50000.50100", "28.1.49000.50000");
    }

    [Fact]
    public void SelectVersion_null_picks_newest()
    {
        var versions = BcArtifactIndex.ParseVersions(CountryJson, PlatformJson);

        BcArtifactIndex.SelectVersion(versions, requested: null).Should().Be("28.2.50931.51727");
    }

    [Fact]
    public void SelectVersion_exact_match_wins()
    {
        var versions = BcArtifactIndex.ParseVersions(CountryJson, PlatformJson);

        BcArtifactIndex.SelectVersion(versions, "28.1.49000.50000").Should().Be("28.1.49000.50000");
    }

    [Fact]
    public void SelectVersion_major_minor_prefix_picks_newest_of_that_minor()
    {
        var versions = BcArtifactIndex.ParseVersions(CountryJson, PlatformJson);

        // Two 28.2 builds — the newer wins.
        BcArtifactIndex.SelectVersion(versions, "28.2").Should().Be("28.2.50931.51727");
    }

    [Fact]
    public void SelectVersion_returns_null_when_nothing_matches()
    {
        var versions = BcArtifactIndex.ParseVersions(CountryJson, PlatformJson);

        BcArtifactIndex.SelectVersion(versions, "99.9").Should().BeNull();
        BcArtifactIndex.SelectVersion(Array.Empty<string>(), requested: null).Should().BeNull();
    }

    [Theory]
    [InlineData("28.2.50931.51727", "28.2")]
    [InlineData("27.5.40000.41000", "27.5")]
    [InlineData("28", "28")]
    public void ToMajorMinor_takes_the_first_two_segments(string version, string expected)
    {
        BcArtifactIndex.ToMajorMinor(version).Should().Be(expected);
    }

    [Fact]
    public void FormatLabel_uses_major_minor_and_upper_country()
    {
        BcArtifactIndex.FormatLabel("28.2.50931.51727", "dk")
            .Should().Be("Business Central 28.2 (DK)");
    }

    [Fact]
    public void BuildApplicationUrl_targets_the_cdn_host()
    {
        var url = BcArtifactIndex.BuildApplicationUrl("28.2.50931.51727", "DK");

        url.Should().Be($"https://{BcArtifactIndex.CdnHost}/onprem/28.2.50931.51727/dk");
    }

    [Fact]
    public void CountryIndexUrl_targets_the_cdn_host_and_lowercases_country()
    {
        // Microsoft 403s the raw blob host; the index is fetched from the CDN.
        BcArtifactIndex.CountryIndexUrl("DK")
            .Should().Be($"https://{BcArtifactIndex.CdnHost}/onprem/indexes/dk.json");
    }

    [Theory]
    // Blob host → CDN (the manifest platformUrl case that was 403-ing).
    [InlineData("https://bcartifacts.blob.core.windows.net/onprem/28.2.50931.51727/platform",
                "https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/onprem/28.2.50931.51727/platform")]
    // Legacy edge host → CDN.
    [InlineData("https://bcartifacts.azureedge.net/onprem/28.2.50931.51727/platform",
                "https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/onprem/28.2.50931.51727/platform")]
    // A stale/other Front Door host → the active CDN.
    [InlineData("https://bcartifacts-stalehash.b02.azurefd.net/onprem/28.2.50931.51727/platform",
                "https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/onprem/28.2.50931.51727/platform")]
    // Already on the active CDN → unchanged.
    [InlineData("https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/onprem/28.2.50931.51727/platform",
                "https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/onprem/28.2.50931.51727/platform")]
    // Foreign host → untouched (download-time trust check then refuses it).
    [InlineData("https://evil.example.com/onprem/x/platform",
                "https://evil.example.com/onprem/x/platform")]
    public void ToCdnUrl_rewrites_only_microsoft_artifact_hosts(string input, string expected)
    {
        BcArtifactIndex.ToCdnUrl(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("bcartifacts.blob.core.windows.net", true)]
    [InlineData("bcartifacts-exdbf9fwegejdqak.b02.azurefd.net", true)]
    [InlineData("bcinsider.blob.core.windows.net", true)]
    [InlineData("evil.example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTrustedArtifactHost_only_allows_microsoft_artifact_hosts(string? host, bool expected)
    {
        BcArtifactIndex.IsTrustedArtifactHost(host).Should().Be(expected);
    }

    [Fact]
    public void DerivePlatformUrl_swaps_the_country_segment_for_platform()
    {
        BcArtifactIndex.DerivePlatformUrl("https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/onprem/28.2.50931.51034/dk")
            .Should().Be("https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/onprem/28.2.50931.51034/platform");
    }

    [Fact]
    public void DerivePlatformUrl_returns_null_for_an_unparseable_url()
    {
        BcArtifactIndex.DerivePlatformUrl("not a url").Should().BeNull();
    }

    [Fact]
    public void ReadPlatformUrl_extracts_the_platform_url_from_a_manifest()
    {
        const string manifest = """
        { "platformUrl": "https://bcartifacts.blob.core.windows.net/onprem/28.2.50931.51727/platform", "version": "28.2" }
        """;

        BcArtifactIndex.ReadPlatformUrl(manifest)
            .Should().Be("https://bcartifacts.blob.core.windows.net/onprem/28.2.50931.51727/platform");
    }

    [Fact]
    public void ReadPlatformUrl_returns_null_when_absent_or_unparseable()
    {
        BcArtifactIndex.ReadPlatformUrl("""{ "version": "28.2" }""").Should().BeNull();
        BcArtifactIndex.ReadPlatformUrl("not json").Should().BeNull();
        BcArtifactIndex.ReadPlatformUrl("").Should().BeNull();
    }

    [Fact]
    public void ParseCountries_reads_the_country_array_lowercased()
    {
        var countries = BcArtifactIndex.ParseCountries("""[ "W1", "dk", "DE" ]""");

        countries.Should().BeEquivalentTo("w1", "dk", "de");
    }
}
