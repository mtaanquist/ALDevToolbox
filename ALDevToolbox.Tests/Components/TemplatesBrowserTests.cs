using ALDevToolbox.Components.Pages;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Smoke test for the canonical list page. Pins the "three states: loading,
/// empty, populated" contract from CLAUDE.md §"Always have the end user in
/// mind" — the service returning an empty list and the page rendering a
/// useful empty state are two different things; only the latter is what the
/// user sees.
/// </summary>
public sealed class TemplatesBrowserTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly TestContext _ctx = new();

    public TemplatesBrowserTests()
    {
        // Authorize attribute: bUnit's TestAuthorizationContext stubs the
        // authentication state. We render as a generic authenticated user;
        // the page doesn't branch on role, only on data.
        var auth = _ctx.AddTestAuthorization();
        auth.SetAuthorized("tester@example.com");

        // Real services against the per-fixture Postgres. Resolving via DI
        // mirrors production wiring rather than mocking a single method
        // (CLAUDE.md: no interfaces just for tests). Bind the ambient context
        // to the IOrganizationContext interface — TemplateService asks for the
        // interface, not the concrete type.
        _ctx.Services.AddSingleton<ALDevToolbox.Services.IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString));
        _ctx.Services.AddScoped<FolderTreeHydrator>();
        _ctx.Services.AddScoped<TemplateService>();
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void Empty_template_set_renders_a_useful_empty_state_with_a_link_to_the_admin_importer()
    {
        var cut = _ctx.RenderComponent<TemplatesBrowser>();

        // Tick the dispatcher so OnInitializedAsync's await on the DB
        // settles before we assert; bUnit's WaitForAssertion handles the
        // microtask hop.
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No templates found");
            cut.Find("a[href='/admin/templates']").Should().NotBeNull(
                "the empty-state copy must offer a path to the recovery action — "
                + "CLAUDE.md §\"three states\" rule");
        });
    }
}
