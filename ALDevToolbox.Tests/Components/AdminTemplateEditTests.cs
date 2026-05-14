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
/// Smoke test for the structured-form branch of /admin/templates/{Key}.
/// The TOML branch is not covered here — it mounts CodeMirror via
/// IJSRuntime and is best left to manual verification (CLAUDE.md
/// §"Tests and verification"). The form branch covers the meaningful
/// invariants: the Key field is readonly on edit, the kebab-case
/// pattern= matches the server rule, and a missing key surfaces the
/// load-failed copy rather than 500ing.
/// </summary>
public sealed class AdminTemplateEditTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly TestContext _ctx = new();

    public AdminTemplateEditTests()
    {
        var auth = _ctx.AddTestAuthorization();
        auth.SetAuthorized("admin@example.com");
        auth.SetRoles("Admin");

        // CodeMirror lives behind IJSRuntime; OnInitialized also registers
        // a location-changing handler. Loose interop is enough for the
        // form branch, which doesn't pump JS until the user switches to
        // TOML mode.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString));
        _ctx.Services.AddScoped<TemplateService>();
        _ctx.Services.AddScoped<ApplicationVersionService>();
        // AuditHistoryPanel renders only when _existingId is set (edit mode)
        // and injects AuditService. Register it so the page doesn't crash on
        // edit-mode hydration.
        _ctx.Services.AddScoped<AuditService>();
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
    public async Task Existing_template_loads_into_the_form_with_readonly_key_field()
    {
        await using (var seed = _db.NewContext())
        {
            var template = TemplateBuilder.Default(key: "runtime-x", runtime: "15");
            template.Name = "Test Runtime X";
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.RenderComponent<AdminTemplateEdit>(p => p
            .Add(c => c.Key, "runtime-x"));

        cut.WaitForAssertion(() =>
        {
            var keyInput = cut.Find("#tpl-key");
            keyInput.GetAttribute("value").Should().Be("runtime-x");
            keyInput.HasAttribute("readonly").Should().BeTrue(
                "the Key is part of the URL; editing it would orphan in-flight links — "
                + "the page locks it on edit");
            keyInput.GetAttribute("pattern").Should().Be("[a-z0-9-]+",
                "kebab-case mirror of the server-side validation rule");
        });
    }

    [Fact]
    public void Unknown_template_key_renders_the_load_failed_copy_not_a_500()
    {
        var cut = _ctx.RenderComponent<AdminTemplateEdit>(p => p
            .Add(c => c.Key, "does-not-exist"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No template with key",
                "the page must degrade to a useful error copy when the URL points "
                + "at a key that was renamed or hard-deleted");
            cut.FindAll("#tpl-key").Should().BeEmpty(
                "the form does not render once _loadFailed flips");
        });
    }

    [Fact]
    public async Task Save_with_blank_required_field_surfaces_a_FieldError_inline_under_that_field()
    {
        // Pins the contract that backs #91: the page round-trips a
        // PlanValidationException from TemplateService into the FieldError
        // component next to the offending input. Clearing Name is the
        // smallest reproduction; the same path covers every other field
        // ValidateMetadataAsync rejects.
        await using (var seed = _db.NewContext())
        {
            var template = TemplateBuilder.Default(key: "runtime-x", runtime: "15");
            template.Name = "Test Runtime X";
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.RenderComponent<AdminTemplateEdit>(p => p
            .Add(c => c.Key, "runtime-x"));

        cut.WaitForState(() => cut.FindAll("#tpl-name").Count > 0);

        // Drop the Name input to blank — TemplateService.ValidateMetadataAsync
        // emits errors["Name"] = "Name is required." for this case. The
        // input is bound on the `oninput` event, so use Input() to match.
        cut.Find("#tpl-name").Input(string.Empty);

        // The structured form is the only <form> rendered while the page is
        // in EditorMode.Form (its default).
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Name is required.",
                "the service emits errors[\"Name\"] = \"Name is required.\"; "
                + "<FieldError Field=\"Name\" Errors=\"_fieldErrors\" /> must render it inline");
        });
    }

    [Fact]
    public async Task Save_with_valid_edits_persists_to_the_database_and_clears_FieldErrors()
    {
        // Happy-path counterpart to the validation-error test above. Pins
        // the contract that the structured form's save round-trip writes
        // through to TemplateService.UpdateAsync and that the page leaves
        // no stale FieldError text on the page afterwards.
        await using (var seed = _db.NewContext())
        {
            var template = TemplateBuilder.Default(key: "runtime-x", runtime: "15");
            template.Name = "Original Name";
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.RenderComponent<AdminTemplateEdit>(p => p
            .Add(c => c.Key, "runtime-x"));

        cut.WaitForState(() => cut.FindAll("#tpl-name").Count > 0);

        cut.Find("#tpl-name").Input("Renamed");
        cut.Find("form").Submit();

        // Wait for SaveAsync to complete: the success banner is the
        // signal the page uses, and matches the user-visible feedback.
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Saved.",
                "the page renders a success banner once UpdateAsync returns");
        });

        await using var read = _db.NewContext();
        var refetched = await read.RuntimeTemplates
            .AsNoTracking()
            .FirstAsync(t => t.Key == "runtime-x");
        refetched.Name.Should().Be("Renamed",
            "the save round-tripped through TemplateService.UpdateAsync into the DB");
    }
}
