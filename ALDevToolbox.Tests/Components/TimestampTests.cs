using ALDevToolbox.Components.Shared;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// <c>&lt;Timestamp&gt;</c> (issue #942): the one place a time is rendered. Text
/// in the organisation's zone, the UTC instant in the title and the datetime
/// attribute.
/// </summary>
public sealed class TimestampTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public TimestampTests()
    {
        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddSingleton(_db.NewContextFactory());
        _ctx.Services.AddScoped<DisplayTimeZone>();
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(NullLogger<>));
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _db.Dispose();
    }

    private Task UseCopenhagenAsync() =>
        _db.NewOrganizationAdminService(_db.NewContext()).SetDisplayTimeZoneAsync("Europe/Copenhagen");

    [Fact]
    public async Task Renders_a_time_element_in_the_org_zone_with_the_UTC_instant_on_hover()
    {
        await UseCopenhagenAsync();
        var value = new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Unspecified);

        var cut = _ctx.Render<Timestamp>(p => p.Add(t => t.Value, value));

        cut.WaitForAssertion(() => cut.MarkupMatches(
            "<time datetime=\"2026-03-29T01:30:00Z\" title=\"2026-03-29 01:30:00 UTC\">2026-03-29 03:30</time>"));
    }

    [Fact]
    public void Without_a_zone_the_text_is_UTC()
    {
        var value = new DateTime(2026, 7, 1, 9, 5, 0, DateTimeKind.Utc);

        var cut = _ctx.Render<Timestamp>(p => p
            .Add(t => t.Value, value)
            .Add(t => t.Format, "d MMM yyyy HH:mm"));

        cut.WaitForAssertion(() => cut.Find("time").TextContent.Should().Be("1 Jul 2026 09:05"));
    }

    [Fact]
    public async Task Relative_uses_the_relative_wording_and_keeps_the_UTC_title()
    {
        await UseCopenhagenAsync();
        var value = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(-5).AddSeconds(-10), DateTimeKind.Utc);

        var cut = _ctx.Render<Timestamp>(p => p
            .Add(t => t.Value, value)
            .Add(t => t.Relative, true));

        cut.WaitForAssertion(() =>
        {
            var time = cut.Find("time");
            time.TextContent.Should().Be("5 minutes ago");
            time.GetAttribute("title").Should().EndWith(" UTC");
            time.GetAttribute("datetime").Should().EndWith("Z");
        });
    }

    [Fact]
    public void A_null_value_renders_the_placeholder()
    {
        var cut = _ctx.Render<Timestamp>(p => p
            .Add(t => t.Value, (DateTime?)null)
            .Add(t => t.Placeholder, "never"));

        cut.WaitForAssertion(() => cut.MarkupMatches("never"));
    }

    [Fact]
    public void A_null_value_without_a_placeholder_renders_nothing()
    {
        var cut = _ctx.Render<Timestamp>(p => p.Add(t => t.Value, (DateTime?)null));

        cut.Markup.Trim().Should().BeEmpty();
    }
}
