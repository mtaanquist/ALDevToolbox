using ALDevToolbox.Components.Shared;
using ALDevToolbox.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Pins the render-time contract of <see cref="Icon"/>. The catalogue test
/// in <c>Icons/IconCatalogTests.cs</c> covers the service-level
/// "known-good / unknown / referenced" matrix; this test covers the
/// component-level "what HTML does the page get" surface — most importantly
/// the missing-icon graceful degradation that issue #47 was opened to fix.
/// </summary>
public sealed class IconTests : IDisposable
{
    private readonly TestContext _ctx = new();

    public IconTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Known_icon_renders_an_svg_with_the_lucide_class_and_inner_markup()
    {
        var cut = _ctx.RenderComponent<Icon>(p => p
            .Add(c => c.Name, "users-round")
            .Add(c => c.Width, 18)
            .Add(c => c.Height, 18));

        var svg = cut.Find("svg");
        svg.GetAttribute("width").Should().Be("18");
        svg.GetAttribute("height").Should().Be("18");
        svg.GetAttribute("class").Should().Contain("lucide-users-round");
        svg.InnerHtml.Trim().Should().NotBeEmpty(
            "a vendored icon must inline its <path>/<circle>/etc. children");
    }

    [Fact]
    public void Missing_icon_renders_an_invisible_placeholder_instead_of_throwing()
    {
        var act = () => _ctx.RenderComponent<Icon>(p => p
            .Add(c => c.Name, "this-icon-does-not-exist"));

        act.Should().NotThrow(
            "issue #47: a missing icon must degrade to a placeholder, "
            + "never a KeyNotFoundException during render");

        var cut = _ctx.RenderComponent<Icon>(p => p
            .Add(c => c.Name, "this-icon-does-not-exist")
            .Add(c => c.Width, 24)
            .Add(c => c.Height, 24));

        cut.FindAll("svg").Should().BeEmpty(
            "the placeholder is a <span>, not an <svg> — the missing-icon path "
            + "must not emit a broken SVG that a screen reader would announce");

        var placeholder = cut.Find("span.icon-missing");
        placeholder.GetAttribute("aria-hidden").Should().Be("true");
        placeholder.GetAttribute("title").Should().Contain("this-icon-does-not-exist");
    }

    [Fact]
    public void Custom_css_is_appended_after_the_default_lucide_classes()
    {
        var cut = _ctx.RenderComponent<Icon>(p => p
            .Add(c => c.Name, "users-round")
            .Add(c => c.Css, "nav-link__icon"));

        var classes = cut.Find("svg").GetAttribute("class") ?? string.Empty;
        classes.Should().Contain("lucide-users-round");
        classes.Should().Contain("nav-link__icon");
    }
}
