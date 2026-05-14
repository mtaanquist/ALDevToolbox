using ALDevToolbox.Components.Shared;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Pins the parity contract called out at the top of
/// <c>MustacheVarsHint.razor</c>: the admin hint must list exactly the
/// subset of placeholders the catalogue marks as
/// <see cref="MustacheVariable.AvailableInAdminContent"/>. If a future
/// change adds (or hides) a variable, the catalogue is the single source of
/// truth and this test fails until the markup re-derives from it.
/// </summary>
public sealed class MustacheVarsHintTests : IDisposable
{
    private readonly TestContext _ctx = new();

    public MustacheVarsHintTests()
    {
        // Icon resolves IconCatalog from DI. Wire the real catalogue (it
        // loads from the main assembly's embedded SVGs) so the child
        // <Icon Name="lightbulb"/> render doesn't blow up — and the
        // missing-icon path stays a server-log warning, not an exception.
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Lists_exactly_the_admin_content_variables_from_the_catalogue()
    {
        var cut = _ctx.RenderComponent<MustacheVarsHint>();

        var rendered = cut.FindAll("dl.mustache-hint__list dt code")
            .Select(e => e.TextContent.Trim())
            .ToList();

        var expected = MustacheVariableCatalog.ForAdminContent
            .Select(v => "{{" + v.Name + "}}")
            .ToList();

        rendered.Should().Equal(expected,
            "the hint must mirror MustacheVariableCatalog.ForAdminContent exactly — "
            + "the comment at the top of MustacheVarsHint.razor calls this out as the "
            + "thing the test prevents from drifting");
    }

    [Fact]
    public void Per_file_only_variables_are_not_surfaced_to_admins()
    {
        var cut = _ctx.RenderComponent<MustacheVarsHint>();

        // {{guid}} is the canonical per-substitution volatile var; embedding
        // it in an always-included file would make the file change on every
        // generation. The catalogue flags it AvailableInAdminContent=false.
        cut.Markup.Should().NotContain("{{guid}}");
        cut.Markup.Should().NotContain("{{namespace}}");
    }

    [Fact]
    public void Renders_a_caption_for_each_listed_variable()
    {
        var cut = _ctx.RenderComponent<MustacheVarsHint>();

        var captions = cut.FindAll("dl.mustache-hint__list dd").Count;
        var terms = cut.FindAll("dl.mustache-hint__list dt").Count;

        terms.Should().Be(captions, "every <dt> must pair with a <dd> caption");
        terms.Should().BeGreaterThan(0, "the catalogue ships with at least one admin-facing variable");
    }
}
