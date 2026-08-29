using ALDevToolbox.Components.Shared;
using ALDevToolbox.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Pins the inline-validation contract of <see cref="IdRangeEditor"/>: the
/// component is the source of the two id-range error messages used by both
/// the New Workspace and New Extension forms. CLAUDE.md §"Always have the
/// end user in mind" requires the HTML-level rules (<c>required</c>,
/// <c>min</c>, <c>type=number</c>) to mirror what the server enforces, and
/// the inline error spans to render at the right thresholds — those are
/// invisible to service-layer tests.
/// </summary>
public sealed class IdRangeEditorTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public IdRangeEditorTests()
    {
        // The inline errors carry a Lucide glyph now, so the component
        // property-injects the icon catalogue.
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Renders_inputs_with_html_validation_attributes_mirroring_the_server_rules()
    {
        var cut = _ctx.Render<IdRangeEditor>(p => p
            .Add(c => c.IdPrefix, "ws-core")
            .Add(c => c.FromName, "CoreFrom")
            .Add(c => c.ToName, "CoreTo")
            .Add(c => c.From, 50000)
            .Add(c => c.To, 50099));

        var from = cut.Find("#ws-core-from");
        from.GetAttribute("type").Should().Be("number");
        from.HasAttribute("required").Should().BeTrue("server requires both endpoints; pattern must match");
        from.GetAttribute("min").Should().Be("1");
        from.GetAttribute("name").Should().Be("CoreFrom");
        from.GetAttribute("value").Should().Be("50000");

        var to = cut.Find("#ws-core-to");
        to.GetAttribute("name").Should().Be("CoreTo");
        to.GetAttribute("value").Should().Be("50099");

        cut.FindAll(".field-error").Should().BeEmpty(
            "a valid range (To > From > 0) renders no inline error");
        cut.Find(".id-range__count").TextContent.Trim().Should().Be("100 IDs",
            "the range is a size, and the count is what saves the user subtracting");
    }

    [Fact]
    public void Renders_inline_error_when_from_is_zero_or_below()
    {
        var cut = _ctx.Render<IdRangeEditor>(p => p
            .Add(c => c.IdPrefix, "x")
            .Add(c => c.FromName, "From")
            .Add(c => c.ToName, "To")
            .Add(c => c.From, 0)
            .Add(c => c.To, 100));

        cut.Markup.Should().Contain("The first ID must be greater than zero.");
    }

    [Fact]
    public void Renders_inline_error_when_to_is_not_greater_than_from()
    {
        var cut = _ctx.Render<IdRangeEditor>(p => p
            .Add(c => c.IdPrefix, "x")
            .Add(c => c.FromName, "From")
            .Add(c => c.ToName, "To")
            .Add(c => c.From, 100)
            .Add(c => c.To, 100));

        cut.Markup.Should().Contain("The last ID must be higher than the first.");
    }

    [Fact]
    public void Oninput_on_the_from_field_raises_FromChanged_with_the_parsed_int()
    {
        int? observed = null;
        var cut = _ctx.Render<IdRangeEditor>(p => p
            .Add(c => c.IdPrefix, "x")
            .Add(c => c.FromName, "From")
            .Add(c => c.ToName, "To")
            .Add(c => c.From, 100)
            .Add(c => c.To, 200)
            .Add(c => c.FromChanged, v => observed = v));

        cut.Find("#x-from").Input("123");

        observed.Should().Be(123);
    }

    [Fact]
    public void Oninput_with_non_numeric_text_does_not_raise_FromChanged()
    {
        int? observed = null;
        var cut = _ctx.Render<IdRangeEditor>(p => p
            .Add(c => c.IdPrefix, "x")
            .Add(c => c.FromName, "From")
            .Add(c => c.ToName, "To")
            .Add(c => c.From, 100)
            .Add(c => c.To, 200)
            .Add(c => c.FromChanged, v => observed = v));

        cut.Find("#x-from").Input("not-a-number");

        observed.Should().BeNull("int.TryParse returns false for non-numeric input");
    }

    [Fact]
    public void Oninput_on_the_to_field_raises_ToChanged_with_the_parsed_int()
    {
        int? observed = null;
        var cut = _ctx.Render<IdRangeEditor>(p => p
            .Add(c => c.IdPrefix, "x")
            .Add(c => c.FromName, "From")
            .Add(c => c.ToName, "To")
            .Add(c => c.From, 100)
            .Add(c => c.To, 200)
            .Add(c => c.ToChanged, v => observed = v));

        cut.Find("#x-to").Input("456");

        observed.Should().Be(456);
    }
}
