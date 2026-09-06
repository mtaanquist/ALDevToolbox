using ALDevToolbox.Services;
using AwesomeAssertions;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.Services;

/// <summary>
/// Unit tests for <see cref="BuildInfo"/>, the release stamp shown in the
/// sidebar footer (issue #604). The rules that matter: an unstamped build
/// renders nothing (never a link to a release it isn't), and a stamped build
/// links to the exact tag.
/// </summary>
public sealed class BuildInfoTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unstamped_builds_have_no_version(string? version)
    {
        BuildInfo.Create(version, "2026-08-24").Should().BeNull();
    }

    [Fact]
    public void Stamped_build_links_to_the_matching_release_tag()
    {
        var info = BuildInfo.Create("6.1.0", "2026-08-24");

        info.Should().NotBeNull();
        info!.Version.Should().Be("6.1.0");
        info.ReleaseUrl.Should().Be("https://github.com/mtaanquist/aldevtoolbox/releases/tag/v6.1.0");
        info.ReleaseDateDisplay.Should().Be("24 August 2026");
        info.HoverTitle.Should().Be("Released 24 August 2026 - release notes");
    }

    [Theory]
    [InlineData("v6.1.0")]
    [InlineData("6.1.0+abc1234")]
    [InlineData(" 6.1.0 ")]
    public void Tag_prefix_build_metadata_and_whitespace_are_stripped(string raw)
    {
        BuildInfo.Create(raw, null)!.Version.Should().Be("6.1.0");
    }

    [Fact]
    public void Unparseable_date_is_dropped_but_the_version_still_renders()
    {
        var info = BuildInfo.Create("6.1.0", "not-a-date");

        info.Should().NotBeNull();
        info!.ReleaseDateDisplay.Should().BeNull();
        info.HoverTitle.Should().Be("Release notes for version 6.1.0");
    }

    [Fact]
    public void Date_is_formatted_invariantly_from_a_full_timestamp()
    {
        BuildInfo.Create("6.1.0", "2026-08-24T22:15:00Z")!
            .ReleaseDateDisplay.Should().Be("24 August 2026");
    }
}
