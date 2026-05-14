using ALDevToolbox.Components.Pages.Admin;
using ALDevToolbox.Services;
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
/// Smoke test for the admin template list. Pins the three-state contract
/// plus the admin-specific "show soft-deleted" toggle from CLAUDE.md's
/// admin-list rule ("admin lists show deprecated and (with a toggle)
/// deleted"). Renders inside the system org so the import-from-catalogue
/// section stays hidden — that branch is a separate concern with its own
/// service path and deserves its own test.
/// </summary>
public sealed class AdminTemplateListTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly TestContext _ctx = new();

    public AdminTemplateListTests()
    {
        // System org context: hides the cross-org import section, leaving
        // the page's main list as the surface under test. Mirrors the
        // bootstrap admin running inside the singleton system org.
        _db.OrgContext.IsSystemOrganization = true;

        var auth = _ctx.AddTestAuthorization();
        auth.SetAuthorized("admin@example.com");
        auth.SetRoles("Admin");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString));
        _ctx.Services.AddScoped<TemplateService>();
        _ctx.Services.AddScoped<TemplateImportService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
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
    public void Empty_template_set_renders_a_useful_empty_state()
    {
        var cut = _ctx.RenderComponent<AdminTemplateList>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No templates yet",
                "the empty-state copy must tell the admin how to recover — "
                + "CLAUDE.md §\"three states\" rule");
            cut.Find("a[href='/admin/templates/new']").Should().NotBeNull(
                "the 'New template' primary action must be reachable from the empty state");
        });
    }

    [Fact]
    public async Task Populated_template_set_renders_one_row_per_template_with_keys_and_runtimes()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default(key: "runtime-15", runtime: "15"));
            seed.RuntimeTemplates.Add(TemplateBuilder.Default(key: "runtime-14", runtime: "14"));
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.RenderComponent<AdminTemplateList>();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("table.data-table tbody tr");
            rows.Should().HaveCount(2);

            var keys = cut.FindAll("table.data-table tbody tr td:nth-child(2) code")
                .Select(e => e.TextContent)
                .ToList();
            keys.Should().Contain(new[] { "runtime-15", "runtime-14" });
        });
    }

    [Fact]
    public async Task Soft_deleted_templates_are_hidden_until_the_show_deleted_checkbox_is_ticked()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default(key: "live"));
            var soft = TemplateBuilder.Default(key: "trashed");
            soft.DeletedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
            seed.RuntimeTemplates.Add(soft);
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.RenderComponent<AdminTemplateList>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("table.data-table tbody tr").Should().HaveCount(1,
                "soft-deleted rows are hidden by default — admins see them only "
                + "after ticking the \"Show soft-deleted\" filter");
            cut.Markup.Should().Contain("live");
            cut.Markup.Should().NotContain(">trashed<");
        });

        // The "Show soft-deleted" toggle is the first checkbox in the toolbar.
        var toggles = cut.FindAll("div.admin-page__toolbar input[type=checkbox]");
        toggles[0].Change(true);

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("table.data-table tbody tr").Should().HaveCount(2);
            cut.Markup.Should().Contain("trashed");
        });
    }
}
