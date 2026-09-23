using ALDevToolbox.Components.Shared;
using ALDevToolbox.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly BunitContext _ctx = new();

    public FieldErrorTests()
    {
        // The message is prefixed by the design system's alert glyph, so the
        // component renders an <Icon> and needs the catalogue.
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Renders_nothing_when_errors_is_null()
    {
        var cut = _ctx.Render<FieldError>(p => p
            .Add(c => c.Field, "name")
            .Add(c => c.Errors, null));

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Renders_nothing_when_the_key_is_absent_from_errors()
    {
        var errors = new Dictionary<string, string> { ["other"] = "boom" };

        var cut = _ctx.Render<FieldError>(p => p
            .Add(c => c.Field, "name")
            .Add(c => c.Errors, errors));

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Renders_the_message_with_alert_role_when_the_key_is_present()
    {
        var errors = new Dictionary<string, string> { ["name"] = "Name is required." };

        var cut = _ctx.Render<FieldError>(p => p
            .Add(c => c.Field, "name")
            .Add(c => c.Errors, errors));

        var span = cut.Find("span.field-error");
        span.GetAttribute("role").Should().Be("alert",
            "screen readers depend on role=alert to announce inline validation errors");
        // The design system's .field-error carries a leading glyph, so assert
        // the message is present rather than that it is the whole content.
        span.TextContent.Should().Contain("Name is required.");
    }

    // ── Plain-message mode (#842) ───────────────────────────────────────

    [Fact]
    public void Renders_a_plain_message_with_the_alert_role_and_the_triangle_glyph()
    {
        var cut = _ctx.Render<FieldError>(p => p.Add(c => c.Message, "Couldn't save."));

        var span = cut.Find("span.field-error");
        span.GetAttribute("role").Should().Be("alert");
        span.TextContent.Should().Contain("Couldn't save.");
        cut.FindAll("svg").Should().ContainSingle("the glyph is chosen once, here, not by each page");
        cut.Markup.Should().Contain("triangle-alert", "the design system's field-error carries the warning triangle");
    }

    [Fact]
    public void Renders_nothing_for_an_empty_message()
    {
        _ctx.Render<FieldError>(p => p.Add(c => c.Message, "")).Markup.Trim().Should().BeEmpty();
        _ctx.Render<FieldError>(p => p.Add(c => c.Message, null)).Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Renders_child_content_when_the_line_needs_markup()
    {
        var cut = _ctx.Render<FieldError>(p => p.AddChildContent("<a href=\"/x\">See who</a>"));

        cut.Find("span.field-error a").TextContent.Should().Be("See who");
    }

    [Fact]
    public void Passes_the_id_and_extra_class_through()
    {
        var cut = _ctx.Render<FieldError>(p => p
            .Add(c => c.Message, "Too long.")
            .Add(c => c.Id, "name-help")
            .Add(c => c.Class, "later"));

        var span = cut.Find("span.field-error");
        span.Id.Should().Be("name-help", "an input points at the line with aria-describedby");
        span.ClassList.Should().Contain("later");
    }
}
