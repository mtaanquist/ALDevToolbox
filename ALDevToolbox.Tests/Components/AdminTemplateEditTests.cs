using Microsoft.AspNetCore.DataProtection;
using ALDevToolbox.Components.Pages.Admin;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private readonly BunitContext _ctx = new();

    /// <summary>
    /// Keeps what the page logged. AdminTemplateEdit catches its own save
    /// exceptions and renders a banner, so without this a failed save is
    /// indistinguishable from a slow one (#739).
    /// </summary>
    private readonly CapturingLoggerProvider _logs = new();

    public AdminTemplateEditTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("admin@example.com");
        auth.SetRoles("Admin");

        // CodeMirror lives behind IJSRuntime; OnInitialized also registers
        // a location-changing handler. Loose interop is enough for the
        // form branch, which doesn't pump JS until the user switches to
        // TOML mode.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        // Factory registration mirrors the app (#741): it also registers
        // AppDbContext itself as scoped, so the page's own services keep the
        // shared context while AuditService resolves its factory.
        _ctx.Services.AddDbContextFactory<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker),
            ServiceLifetime.Scoped);
        _ctx.Services.AddScoped<FolderTreeHydrator>();
        _ctx.Services.AddScoped<TemplateService>();
        _ctx.Services.AddScoped<ApplicationVersionService>();
        // The page now lists the org's always-included files via a checkbox
        // section, so it pulls in OrganizationConfigService. Wire the same
        // storage chain the service depends on so the bunit render doesn't
        // throw "no registered service" mid-paint.
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddDataProtection();
        // AuditHistoryPanel renders only when _existingId is set (edit mode)
        // and injects AuditService. Register it so the page doesn't crash on
        // edit-mode hydration.
        _ctx.Services.AddScoped<AuditService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddProvider(_logs));
        _ctx.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(loggerFactory);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Logger<>));
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
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

        var cut = _ctx.Render<AdminTemplateEdit>(p => p
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
        var cut = _ctx.Render<AdminTemplateEdit>(p => p
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
    public async Task Save_with_valid_edits_persists_to_the_database_and_clears_FieldErrors()
    {
        // Pins the structured-form save round-trip: load → mutate → submit
        // → TemplateService.UpdateAsync → DB. The page renders the "Saved."
        // banner on success, and #83 (split AdminTemplateEdit.razor) must
        // preserve this contract.
        //
        // The validation-error counterpart lives at the service level
        // (TemplateServiceReconciliationTests covers ValidateAsync's
        // field-keyed errors directly); a page-level equivalent is more
        // appropriate on the AdminCatalog form (see AdminCatalogTests), which
        // has a simpler save flow with the same <FieldError> render contract.
        await using (var seed = _db.NewContext())
        {
            var template = TemplateBuilder.Default(key: "runtime-x", runtime: "15");
            template.Name = "Original Name";
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<AdminTemplateEdit>(p => p
            .Add(c => c.Key, "runtime-x"));

        cut.WaitForState(() => cut.FindAll("#tpl-name").Count > 0);

        // The page's own hydration must finish before the form is submitted.
        // AuditHistoryPanel used to load on the same scoped DbContext the save
        // uses, so submitting while "Loading history..." was still up started a
        // second operation on that context and the save threw (#739) - about one
        // CI run in six. #741 closed that hazard for the audit panel: it now
        // reads through its own short-lived context. The wait stays because a
        // real user does wait for the page to settle, and it documents the
        // history.
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Loading history...",
            "the page is not idle until its history panel has loaded"));

        cut.Find("#tpl-name").Input("Renamed");
        cut.Find("form").Submit();

        // Wait for SaveAsync to reach *either* outcome, then assert it was the
        // success one. Waiting only for "Saved." meant a failed save burned the
        // full 30-second bUnit timeout and reported "the assertion did not pass",
        // hiding the exception the page had already caught and rendered (#739).
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Match(
                m => m.Contains("Saved.") || m.Contains("The change was not saved."),
                "SaveAsync renders one banner or the other when it finishes");
        });

        cut.Markup.Should().Contain("Saved.",
            "the save must succeed; the page reported a failure instead. What it logged:\n"
            + _logs.ErrorsForFailureMessage());

        await using var read = _db.NewContext();
        var refetched = await read.RuntimeTemplates
            .AsNoTracking()
            .FirstAsync(t => t.Key == "runtime-x");
        refetched.Name.Should().Be("Renamed",
            "the save round-tripped through TemplateService.UpdateAsync into the DB");
    }
}
