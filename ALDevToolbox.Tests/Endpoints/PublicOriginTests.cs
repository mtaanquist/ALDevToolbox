using ALDevToolbox.Endpoints;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// PUBLIC_BASE_URL is what stops a credential email link being built from the
/// attacker-supplied Host header (issue #670). The parser must normalise the
/// value so callers can concatenate a rooted path, refuse anything that isn't
/// an http(s) origin, and never throw on a typo — a bad value falls back to
/// the old behaviour with a warning rather than stopping the app booting.
/// </summary>
public class PublicOriginTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnsetYieldsNull(string? raw)
    {
        PublicOrigin.Parse(raw).Should().BeNull();
    }

    [Theory]
    [InlineData("https://toolbox.cronus.example")]
    [InlineData("http://localhost:8080")]
    [InlineData("https://toolbox.cronus.example:8443")]
    public void AcceptsHttpAndHttpsOrigins(string raw)
    {
        PublicOrigin.Parse(raw).Should().Be(raw);
    }

    [Theory]
    [InlineData("https://toolbox.cronus.example/", "https://toolbox.cronus.example")]
    [InlineData("  https://toolbox.cronus.example//  ", "https://toolbox.cronus.example")]
    [InlineData("https://toolbox.cronus.example/base/", "https://toolbox.cronus.example/base")]
    public void StripsTrailingSlashAndSurroundingWhitespace(string raw, string expected)
    {
        PublicOrigin.Parse(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("ftp://toolbox.cronus.example")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("toolbox.cronus.example")]
    [InlineData("//toolbox.cronus.example")]
    public void RejectsAnythingThatIsNotAnHttpOrigin(string raw)
    {
        PublicOrigin.Parse(raw).Should().BeNull();
    }

    [Fact]
    public void UnconfiguredOriginReportsItself()
    {
        var origin = new PublicOrigin(null);

        origin.IsConfigured.Should().BeFalse();
        origin.Configured.Should().BeNull();
    }

    [Fact]
    public void ConfiguredOriginReportsItself()
    {
        var origin = new PublicOrigin("https://toolbox.cronus.example");

        origin.IsConfigured.Should().BeTrue();
        origin.Configured.Should().Be("https://toolbox.cronus.example");
    }
}
