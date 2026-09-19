using ALDevToolbox.Components.Shared;
using ALDevToolbox.Services;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

public sealed class SelectBoxTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public SelectBoxTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void The_wrapper_holds_the_select_and_then_the_caret_nobody_has_to_remember()
    {
        var cut = _ctx.Render<SelectBox>(p => p
            .AddChildContent("<select class=\"select\"><option>One</option></select>"));

        var wrap = cut.Find("span.select-wrap");
        wrap.Children.Select(c => c.TagName.ToLowerInvariant()).Should().Equal("select", "svg");
        wrap.QuerySelector("svg")!.ClassList.Should().Contain("select-wrap__caret");
    }

    [Fact]
    public void A_width_class_and_other_attributes_land_on_the_wrapper()
    {
        var cut = _ctx.Render<SelectBox>(p => p
            .Add(s => s.Class, "upg-view")
            .AddUnmatched("title", "Which environments to show"));

        var wrap = cut.Find("span.select-wrap");
        wrap.GetAttribute("class").Should().Be("select-wrap upg-view");
        wrap.GetAttribute("title").Should().Be("Which environments to show");
    }
}
