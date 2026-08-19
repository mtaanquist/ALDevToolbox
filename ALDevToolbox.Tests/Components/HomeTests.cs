using ALDevToolbox.Components.Pages;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Tools;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Smoke tests for the launcher at <c>/</c>.
///
/// The tile list is the pin that matters. This page silently omitted four of
/// the ten tools for as long as they have existed - they were in the sidebar
/// and never here - so a test that counts tiles is what stops the next tool
/// shipping half-linked. The sign-in destinations are pinned for the same
/// reason: two of them pointed at the wrong page and at a 404, and nothing
/// about the markup looked wrong.
/// </summary>
public sealed class HomeTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly TestContext _ctx = new();
    private readonly ToolAvailabilityState _tools;

    public HomeTests()
    {
        _db.McpAvailability.Set(true);
        _tools = new ToolAvailabilityState(_db.McpAvailability);

        _ctx.Services.AddSingleton<IToolAvailability>(_tools);
        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<AppDbContext>(opts => opts.UseNpgsql(_db.ConnectionString));
        _ctx.Services.AddScoped<DashboardService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void Every_tool_in_the_sidebar_has_a_tile_on_the_launcher()
    {
        Authorize();

        var cut = _ctx.RenderComponent<Home>();

        cut.WaitForAssertion(() =>
        {
            var hrefs = cut.FindAll(".tool-tile").Select(t => t.GetAttribute("href")).ToList();
            hrefs.Should().BeEquivalentTo(new[]
            {
                "/templates/workspace", "/templates/extension", "/templates",
                "/cookbook", "/object-explorer",
                "/projects", "/pipelines", "/releases",
                "/translator", "/compare", "/piper", "/tools/mcp",
            });
        });
    }

    [Fact]
    public void Signed_out_tiles_lead_back_to_the_tool_they_advertise()
    {
        Anonymous();

        var cut = _ctx.RenderComponent<Home>();

        cut.WaitForAssertion(() =>
        {
            var hrefs = cut.FindAll(".tool-tile").Select(t => t.GetAttribute("href")).ToList();
            // The generators are the two that were wrong: Workspace pointed at
            // the Projects tool's create page and Extension at a route that does
            // not exist, so signing in from either landed somewhere else.
            hrefs.Should().Contain("/login?returnUrl=%2Ftemplates%2Fworkspace");
            hrefs.Should().Contain("/login?returnUrl=%2Ftemplates%2Fextension");
            hrefs.Should().NotContain(h => h!.Contains("%2Fprojects%2Fnew"));
            hrefs.Should().NotContain(h => h!.Contains("%2Fprojects%2Fextension"));
            // Compare needs no account, so it is the one tile that links straight through.
            hrefs.Should().Contain("/compare");
        });
    }

    [Fact]
    public void A_group_whose_tools_are_all_switched_off_takes_its_heading_with_it()
    {
        Authorize();
        _tools.Set(new[] { ToolKey.Projects, ToolKey.Pipelines, ToolKey.Releases });

        var cut = _ctx.RenderComponent<Home>();

        cut.WaitForAssertion(() =>
        {
            // A heading over nothing is worse than no heading: it reads as a
            // section that failed to load.
            cut.FindAll(".section-label").Select(l => l.TextContent.Trim())
                .Should().BeEquivalentTo(new[] { "Build", "Work with text", "Connect an assistant" });
        });
    }

    [Fact]
    public void An_empty_organisation_says_so_on_the_tile_instead_of_showing_a_zero()
    {
        Authorize();

        var cut = _ctx.RenderComponent<Home>();

        cut.WaitForAssertion(() =>
        {
            var metas = cut.FindAll(".tool-tile__meta").Select(m => m.TextContent.Trim()).ToList();
            // "0 recipes" is a fact; the second half is the next step. Every
            // empty meta names one — a tile that only states the void leaves the
            // user to guess who fills it.
            metas.Should().Contain("None yet - ask an admin to import some");
            metas.Should().Contain("No recipes yet - suggest the first one");
            metas.Should().Contain("No BC releases yet - ask an admin to import one");
            metas.Should().Contain("No projects yet - add your first");
        });
    }

    [Fact]
    public void A_populated_organisation_counts_only_what_a_user_can_pick()
    {
        Authorize();
        using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("pickable"));
            var deprecated = TemplateBuilder.Default("deprecated");
            deprecated.Deprecated = true;
            seed.RuntimeTemplates.Add(deprecated);
            seed.SaveChanges();
        }

        var cut = _ctx.RenderComponent<Home>();

        cut.WaitForAssertion(() =>
            cut.FindAll(".tool-tile__meta").Select(m => m.TextContent.Trim())
                .Should().Contain("1 template"));
    }

    /// <summary>Registers the auth services and leaves the visitor signed out.</summary>
    private void Anonymous() => _ctx.AddTestAuthorization().SetNotAuthorized();

    private void Authorize()
    {
        var auth = _ctx.AddTestAuthorization();
        auth.SetAuthorized("user@cronus.example");
        auth.SetRoles("User");
    }
}
