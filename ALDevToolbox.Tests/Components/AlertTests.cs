using ALDevToolbox.Components.Shared;
using ALDevToolbox.Services;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Pins what <see cref="Alert"/> decides so that pages no longer do: the icon and the
/// role each tone gets, and the element it renders as.
/// </summary>
public sealed class AlertTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public AlertTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    [Theory]
    [InlineData(AlertTone.Danger, "alert alert--danger", "alert")]
    [InlineData(AlertTone.Warn, "alert alert--warn", "status")]
    [InlineData(AlertTone.Success, "alert alert--success", "status")]
    [InlineData(AlertTone.Info, "alert alert--info", null)]
    [InlineData(AlertTone.Neutral, "alert", null)]
    public void The_tone_decides_the_class_and_the_role(AlertTone tone, string cssClass, string? role)
    {
        var cut = _ctx.Render<Alert>(p => p.Add(a => a.Tone, tone).AddChildContent("<span>Saved.</span>"));

        var box = cut.Find("p");
        box.GetAttribute("class").Should().Be(cssClass);
        box.GetAttribute("role").Should().Be(role,
            "an error interrupts, a confirmation or a warning waits its turn, and a standing note is not a live region");
        box.QuerySelector("span")!.TextContent.Should().Be("Saved.");
    }

    [Fact]
    public void Every_tone_but_neutral_leads_with_an_icon_and_the_content_follows_it()
    {
        _ctx.Render<Alert>(p => p.Add(a => a.Tone, AlertTone.Danger).AddChildContent("<span>x</span>"))
            .Find("p").Children.Select(c => c.TagName.ToLowerInvariant()).Should().Equal("svg", "span");

        _ctx.Render<Alert>(p => p.Add(a => a.Tone, AlertTone.Neutral).AddChildContent("<span>x</span>"))
            .FindAll("svg").Should().BeEmpty();
    }

    [Fact]
    public void Icon_and_role_can_be_replaced_or_removed()
    {
        var cut = _ctx.Render<Alert>(p => p
            .Add(a => a.Tone, AlertTone.Danger)
            .Add(a => a.Icon, "")
            .Add(a => a.Role, "")
            .AddChildContent("<span>x</span>"));

        cut.FindAll("svg").Should().BeEmpty();
        cut.Find("p").HasAttribute("role").Should().BeFalse();

        _ctx.Render<Alert>(p => p.Add(a => a.Tone, AlertTone.Info).Add(a => a.Role, "status"))
            .Find("p").GetAttribute("role").Should().Be("status");
    }

    [Fact]
    public void Block_renders_a_div_and_extra_classes_and_attributes_pass_through()
    {
        var cut = _ctx.Render<Alert>(p => p
            .Add(a => a.Tone, AlertTone.Warn)
            .Add(a => a.Block, true)
            .Add(a => a.Class, "site-banner")
            .AddUnmatched("data-job-error", "")
            .AddChildContent("<ul><li>x</li></ul>"));

        cut.FindAll("p").Should().BeEmpty("a paragraph cannot hold a list");
        var box = cut.Find("div");
        box.GetAttribute("class").Should().Be("alert alert--warn site-banner");
        box.HasAttribute("data-job-error").Should().BeTrue();
    }
}
