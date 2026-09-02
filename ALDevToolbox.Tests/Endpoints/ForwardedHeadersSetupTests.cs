using System.Net;
using ALDevToolbox.Endpoints;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// TRUSTED_PROXIES is the fence that stops any client from setting its own
/// X-Forwarded-For and thereby choosing the partition key for the per-IP login
/// and DCR rate limiters (issue #672). The parser must be strict about what it
/// trusts, and must never throw on a typo — a bad entry is dropped, not fatal.
/// </summary>
public class ForwardedHeadersSetupTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",, ,")]
    public void BlankInputTrustsNothing(string? raw)
    {
        var result = ForwardedHeadersSetup.Parse(raw);

        result.IsEmpty.Should().BeTrue();
        result.Invalid.Should().BeEmpty();
    }

    [Fact]
    public void ParsesSingleAddresses()
    {
        var result = ForwardedHeadersSetup.Parse("10.0.0.5, ::1");

        result.Proxies.Should().BeEquivalentTo([IPAddress.Parse("10.0.0.5"), IPAddress.Parse("::1")]);
        result.Networks.Should().BeEmpty();
        result.Invalid.Should().BeEmpty();
    }

    [Fact]
    public void ParsesCidrNetworks()
    {
        var result = ForwardedHeadersSetup.Parse("172.16.0.0/12 fd00::/8");

        result.Networks.Should().HaveCount(2);
        result.Networks[0].BaseAddress.Should().Be(IPAddress.Parse("172.16.0.0"));
        result.Networks[0].PrefixLength.Should().Be(12);
        result.Proxies.Should().BeEmpty();
    }

    [Fact]
    public void CollectsGarbageEntriesInsteadOfThrowing()
    {
        var result = ForwardedHeadersSetup.Parse("10.0.0.5, not-an-ip, 10.0.0.0/99, 192.168.1.0/24");

        result.Proxies.Should().ContainSingle().Which.Should().Be(IPAddress.Parse("10.0.0.5"));
        result.Networks.Should().ContainSingle();
        result.Invalid.Should().BeEquivalentTo(["not-an-ip", "10.0.0.0/99"]);
    }

    [Fact]
    public void ApplyKeepsFrameworkLoopbackDefaults()
    {
        var options = new ForwardedHeadersOptions();
        var loopbackDefaults = options.KnownIPNetworks.Count + options.KnownProxies.Count;
        loopbackDefaults.Should().BeGreaterThan(0, "the framework trusts loopback out of the box");

        ForwardedHeadersSetup.Apply(options, ForwardedHeadersSetup.Parse("10.0.0.5, 172.16.0.0/12"));

        options.KnownProxies.Should().Contain(IPAddress.Parse("10.0.0.5"));
        options.KnownIPNetworks.Should().Contain(n => n.PrefixLength == 12);
        (options.KnownIPNetworks.Count + options.KnownProxies.Count).Should().Be(loopbackDefaults + 2);
    }
}
