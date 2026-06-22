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
/// Smoke test for the well-known dependency catalogue editor — a table where
/// each entry is one row of inline inputs and a permanent blank "ghost" row at
/// the bottom is the add affordance (typing in it spawns the next blank row).
/// The test also pins the GUID <c>pattern=</c> attribute on the DepId input,
/// since that's the HTML mirror of the server-side validation rule.
/// </summary>
public sealed class AdminCatalogTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly TestContext _ctx = new();

    public AdminCatalogTests()
    {
        // The editor registers an Excel-paste handler via JS interop in
        // OnAfterRenderAsync; loose mode lets those calls no-op under bunit.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

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
    public void Empty_catalogue_renders_a_single_blank_ghost_row_and_no_add_button()
    {
        var cut = _ctx.RenderComponent<AdminCatalog>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("tbody tr").Should().HaveCount(1,
                "an empty catalogue still shows the always-present blank ghost row "
                + "as the add affordance");
            cut.FindAll("button").Any(b => b.TextContent.Contains("Add row"))
                .Should().BeFalse("the explicit Add button was replaced by the inline ghost row");
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
            // Two seeded rows plus the always-present trailing ghost row.
            var rows = cut.FindAll("tbody tr");
            rows.Should().HaveCount(3);

            // The first text input on each row is the DepId. It must carry
            // the GUID pattern so the browser surfaces the same rule the
            // server enforces — CLAUDE.md §"Always have the end user in mind"
            // requires the two to stay in sync.
            var firstInput = cut.Find("tbody tr td input[type=text]");
            firstInput.HasAttribute("pattern").Should().BeTrue();
            firstInput.GetAttribute("pattern").Should().Contain("[0-9a-fA-F]",
                "the pattern must accept hex GUIDs in 8-4-4-4-12 form");
        });
    }

    [Fact]
    public void Typing_into_the_ghost_row_then_submitting_shows_inline_errors_for_the_missing_fields()
    {
        // Pins the contract that backs #91: when the service throws a
        // PlanValidationException with a field-keyed error dictionary, the page
        // surfaces each message inline via <FieldError>. With the ghost-row
        // design a fully-blank row is dropped on save, so we make the row real
        // by filling one cell (Name) and leave the other required fields empty —
        // those three should bounce back, but not Name.
        var cut = _ctx.RenderComponent<AdminCatalog>();

        // Empty catalogue still renders the single blank ghost row.
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(1));

        // Type into the ghost row's Name cell (data-col=1). A fresh ghost
        // appears below, so the table grows to two rows.
        cut.Find("tbody tr td input[data-col='1']").Input("ForNAV Core");
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));

        // Submit; CatalogService.SaveAsync collects one error per missing
        // required field on the now-real row and throws PlanValidationException.
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Dependency id is required.",
                "DepId is empty → CatalogService keys the error under Entries[0].DepId → "
                + "<FieldError Field='Entries[0].DepId'> renders the message inline");
            cut.Markup.Should().Contain("Dependency publisher is required.");
            cut.Markup.Should().Contain("Default version is required.");
            cut.Markup.Should().NotContain("Dependency name is required.",
                "the Name cell was filled, so only the other three required fields bounce back");
            cut.Markup.Should().Contain("3 validation error(s)",
                "the top-of-form summary copy counts the errors so the user "
                + "knows what to scroll back to");
        });
    }
}
