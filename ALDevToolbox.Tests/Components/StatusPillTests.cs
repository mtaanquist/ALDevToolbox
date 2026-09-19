using ALDevToolbox.Components.Shared;
using AwesomeAssertions;
using Bunit;

namespace ALDevToolbox.Tests.Components;

public sealed class StatusPillTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void The_pill_takes_its_tone_and_always_carries_the_dot_before_its_content()
    {
        var cut = _ctx.Render<StatusPill>(p => p.Add(s => s.Tone, "running").AddChildContent("Running"));

        var pill = cut.Find("span.status-pill");
        pill.GetAttribute("class").Should().Be("status-pill status-pill--running");
        // The sheet hides the dot on every tone but live and running. A hand-written pill
        // that left it out looked right until its tone was computed and came out running.
        pill.FirstElementChild!.ClassList.Should().Contain("status-pill__dot");
        pill.TextContent.Should().Be("Running");
    }

    [Fact]
    public void Extra_classes_and_attributes_pass_through_and_a_null_class_adds_nothing()
    {
        var cut = _ctx.Render<StatusPill>(p => p
            .Add(s => s.Tone, "muted")
            .Add(s => s.Class, "upg-prod")
            .AddUnmatched("title", "Production environment"));
        cut.Find("span.status-pill").GetAttribute("class").Should().Be("status-pill status-pill--muted upg-prod");
        cut.Find("span.status-pill").GetAttribute("title").Should().Be("Production environment");

        _ctx.Render<StatusPill>(p => p.Add(s => s.Tone, "muted").Add(s => s.Class, null))
            .Find("span.status-pill").GetAttribute("class").Should().Be("status-pill status-pill--muted");
    }
}
