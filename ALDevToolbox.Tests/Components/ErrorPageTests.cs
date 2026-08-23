using System.Reflection;
using ALDevToolbox.Components.Layout;
using ALDevToolbox.Components.Pages;
using ALDevToolbox.Components.Shared;
using ALDevToolbox.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Pins the fix for issue #559: <c>UseExceptionHandler("/Error")</c> re-executes
/// this page after a request has already failed, and the failure it matters most
/// for is an unreachable database. Under the default <see cref="MainLayout"/> the
/// error page's own layout read the org name from the database and threw, so the
/// reader got the framework's bare unhandled-exception response instead.
///
/// The guard is structural: nothing on this page's render path may resolve a
/// database-backed service. The test container below therefore registers only the
/// icon catalogue (an in-memory singleton over embedded SVGs) — no
/// <c>AppDbContext</c>, no <c>OrganizationConfigService</c>. If either layout or
/// page ever grows such a dependency, the render throws here rather than in
/// production during an outage.
/// </summary>
public sealed class ErrorPageTests : IDisposable
{
    private readonly TestContext _ctx = new();

    public ErrorPageTests() =>
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Error_page_opts_out_of_the_default_layout()
    {
        var layout = typeof(Error).GetCustomAttribute<LayoutAttribute>()?.LayoutType;

        layout.Should().Be(typeof(MinimalLayout),
            "the default layout queries the database, which is exactly what /Error has to survive");
    }

    [Fact]
    public void Error_page_renders_inside_its_layout_with_no_database_services_registered()
    {
        var cut = _ctx.RenderComponent<LayoutView>(p => p
            .Add(c => c.Layout, typeof(MinimalLayout))
            .Add(c => c.ChildContent, (RenderFragment)(builder =>
            {
                builder.OpenComponent<Error>(0);
                builder.CloseComponent();
            })));

        cut.Find("h1").TextContent.Should().Be("Something went wrong");
        cut.Markup.Should().Contain("AL Dev Toolbox",
            "the error page still has to read as part of the app");
        cut.FindAll("a[href=\"/\"]").Should().NotBeEmpty(
            "the shell-less layout removes the sidebar, so the page owns the way back");
    }
}
