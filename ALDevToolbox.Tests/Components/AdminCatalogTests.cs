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
/// Smoke test for the well-known dependency catalogue editor. Unlike the
/// other admin lists this is a form, not a table — the empty state lives
/// inside the form and the "Add entry" button is the recovery action. The
/// test also pins the GUID <c>pattern=</c> attribute on the DepId input,
/// since that's the HTML mirror of the server-side validation rule.
/// </summary>
public sealed class AdminCatalogTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly TestContext _ctx = new();

    public AdminCatalogTests()
    {
        var auth = _ctx.AddTestAuthorization();
        auth.SetAuthorized("admin@example.com");
        auth.SetRoles("Admin");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString));
        _ctx.Services.AddScoped<CatalogService>();
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
    public void Empty_catalogue_renders_a_recovery_message_pointing_at_the_add_button()
    {
        var cut = _ctx.RenderComponent<AdminCatalog>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No catalogue entries");
            cut.Markup.Should().Contain("Add entry",
                "the empty-state copy must name the recovery action — the same "
                + "button is rendered above the empty message");
        });
    }

    [Fact]
    public async Task Populated_catalogue_renders_one_row_per_entry_with_a_guid_pattern_on_the_dep_id_input()
    {
        await using (var seed = _db.NewContext())
        {
            seed.WellKnownDependencies.Add(WellKnownDependencyBuilder
                .ForNav("12345678-1234-1234-1234-1234567890ab", "ForNAV Core"));
            seed.WellKnownDependencies.Add(WellKnownDependencyBuilder
                .ForNav("abcdef01-2345-6789-abcd-ef0123456789", "ForNAV Reporting", ordering: 1));
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.RenderComponent<AdminCatalog>();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("div.folder-editor__row");
            rows.Should().HaveCount(2);

            // The first text input on each row is the DepId. It must carry
            // the GUID pattern so the browser surfaces the same rule the
            // server enforces — CLAUDE.md §"Always have the end user in mind"
            // requires the two to stay in sync.
            var firstInput = cut.Find("div.folder-editor__path input[type=text]");
            firstInput.HasAttribute("pattern").Should().BeTrue();
            firstInput.GetAttribute("pattern").Should().Contain("[0-9a-fA-F]",
                "the pattern must accept hex GUIDs in 8-4-4-4-12 form");
        });
    }
}
