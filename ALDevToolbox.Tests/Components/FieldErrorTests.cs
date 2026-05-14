using ALDevToolbox.Components.Shared;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Pins the "render nothing when key is absent, render the message when
/// present" contract that every form on the site depends on for inline
/// validation. CLAUDE.md §"Always have the end user in mind": validation
/// errors come back from services as field-keyed dictionaries and the UI
/// renders them next to the field — silently dropping a message because the
/// key was misspelled is a class of bug worth a one-line test.
/// </summary>
public sealed class FieldErrorTests : IDisposable
{
    private readonly TestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Renders_nothing_when_errors_is_null()
    {
        var cut = _ctx.RenderComponent<FieldError>(p => p
            .Add(c => c.Field, "name")
            .Add(c => c.Errors, null));

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Renders_nothing_when_the_key_is_absent_from_errors()
    {
        var errors = new Dictionary<string, string> { ["other"] = "boom" };

        var cut = _ctx.RenderComponent<FieldError>(p => p
            .Add(c => c.Field, "name")
            .Add(c => c.Errors, errors));

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Renders_the_message_with_alert_role_when_the_key_is_present()
    {
        var errors = new Dictionary<string, string> { ["name"] = "Name is required." };

        var cut = _ctx.RenderComponent<FieldError>(p => p
            .Add(c => c.Field, "name")
            .Add(c => c.Errors, errors));

        var span = cut.Find("span.form-field-error");
        span.GetAttribute("role").Should().Be("alert",
            "screen readers depend on role=alert to announce inline validation errors");
        span.TextContent.Should().Be("Name is required.");
    }
}
