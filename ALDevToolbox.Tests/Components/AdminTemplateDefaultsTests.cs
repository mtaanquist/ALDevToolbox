using Microsoft.AspNetCore.DataProtection;
using ALDevToolbox.Components.Pages.Admin;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Smoke test for the per-org defaults editor. Pins the load → pre-fill
/// round-trip — the page reads from <see cref="OrganizationConfigService"/>
/// and is supposed to hydrate every input from the persisted settings row.
/// CLAUDE.md §"Always have the end user in mind" requires the form's HTML
/// min/required attributes to mirror the server rules.
/// </summary>
public sealed class AdminTemplateDefaultsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public AdminTemplateDefaultsTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("admin@example.com");
        auth.SetRoles("Admin");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddSingleton<IMemoryCache>(new MemoryCache(Options.Create(new MemoryCacheOptions())));
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddScoped<OrganizationBrandingService>();
        _ctx.Services.AddDataProtection();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task Settings_row_pre_fills_every_input_in_the_form()
    {
        await using (var seed = _db.NewContext())
        {
            seed.OrganizationSettings.Add(new OrganizationSettings
            {
                OrganizationId = TestDb.DefaultOrgId,
                DefaultPublisher = "Acme",
                DefaultIdRangeFrom = 80000,
                DefaultIdRangeTo = 80999,
                DefaultBrief = "Acme customisations.",
                DefaultCoreDescription = "Long description here.",
            });
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<AdminTemplateDefaults>();

        // The page shows "Loading…" until OnInitializedAsync resolves three
        // DB reads inside OrganizationConfigService. WaitForState is cheaper
        // than WaitForAssertion (no exception suppression) and points at the
        // exact transition we care about.
        cut.WaitForState(() => cut.FindAll("#cfg-publisher").Any(), TimeSpan.FromSeconds(5));

        cut.Find("#cfg-publisher").GetAttribute("value").Should().Be("Acme");
        cut.Find("#cfg-from").GetAttribute("value").Should().Be("80000");
        cut.Find("#cfg-to").GetAttribute("value").Should().Be("80999");
        cut.Find("#cfg-brief").GetAttribute("value").Should().Be("Acme customisations.");
        // Blazor's @bind on a textarea emits the value via the value= attribute
        // rather than inner content (the runtime sets the property after the
        // browser hydrates); TextContent is empty in the initial render.
        cut.Find("#cfg-desc").GetAttribute("value").Should().Be("Long description here.");
    }

    [Fact]
    public async Task The_naming_section_pre_fills_from_the_settings_row_and_saves()
    {
        await using (var seed = _db.NewContext())
        {
            seed.OrganizationSettings.Add(new OrganizationSettings
            {
                OrganizationId = TestDb.DefaultOrgId,
                DefaultPublisher = "CRONUS A/S",
                DefaultIdRangeFrom = 80000,
                DefaultIdRangeTo = 80999,
                NamingFolderStyle = NamingStyle.Lowercase,
                NamingRepositoryStyle = NamingStyle.SnakeCase,
                ExtensionPrefixMode = ExtensionPrefixMode.Fixed,
                ExtensionPrefix = "PARTNER",
                UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<AdminTemplateDefaults>();
        cut.WaitForState(() => cut.FindAll("#cfg-folder-style").Any(), TimeSpan.FromSeconds(5));

        cut.Find("#cfg-folder-style").GetAttribute("value").Should().Be(nameof(NamingStyle.Lowercase));
        cut.Find("#cfg-repo-style").GetAttribute("value").Should().Be(nameof(NamingStyle.SnakeCase));
        cut.Find("#cfg-prefix-mode").GetAttribute("value").Should().Be(nameof(ExtensionPrefixMode.Fixed));
        cut.Find("#cfg-prefix").GetAttribute("value").Should().Be("PARTNER");
        // Every option is labelled with what it produces, worked through
        // CustomerNaming rather than described in prose.
        cut.Markup.Should().Contain("jorgensen_mobler (snake_case)");
        cut.Markup.Should().Contain("JorgensenMobler (PascalCase)");

        // Change one style and save: the row has to carry the new value.
        cut.Find("#cfg-folder-style").Change(nameof(NamingStyle.KebabCase));
        cut.Find(".form-actions .btn--primary").Click();

        // The click hands off to an async save; poll rather than assume it has
        // landed by the time the call returns.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        OrganizationSettings row;
        while (true)
        {
            await using var check = _db.NewContext();
            row = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .FirstAsync(check.OrganizationSettings);
            if (row.NamingFolderStyle == NamingStyle.KebabCase || DateTime.UtcNow > deadline) break;
            await Task.Delay(100);
        }

        row.NamingFolderStyle.Should().Be(NamingStyle.KebabCase);
        row.NamingRepositoryStyle.Should().Be(NamingStyle.SnakeCase);
        row.ExtensionPrefixMode.Should().Be(ExtensionPrefixMode.Fixed);
        row.ExtensionPrefix.Should().Be("PARTNER");
    }

    [Fact]
    public void A_prefix_that_every_workspace_needs_is_marked_required_and_refused_inline()
    {
        var cut = _ctx.Render<AdminTemplateDefaults>();
        cut.WaitForState(() => cut.FindAll("#cfg-prefix-mode").Any(), TimeSpan.FromSeconds(5));

        cut.Find("#cfg-prefix-mode").Change(nameof(ExtensionPrefixMode.Fixed));
        cut.WaitForAssertion(() =>
            cut.Find("#cfg-prefix").HasAttribute("required").Should().BeTrue(
                "OrganizationConfigService refuses a blank prefix under this policy; "
                + "CLAUDE.md has the form mirror the server rule"));

        // Something else has to be dirty as well, or Save is disabled - the
        // publisher is required too, so fill it the way an admin would.
        cut.Find("#cfg-publisher").Input("CRONUS A/S");
        cut.Find("#cfg-from").Input("50000");
        cut.Find("#cfg-to").Input("50999");
        cut.Find(".form-actions .btn--primary").Click();

        // The refusal lands beside the field, not only in the alert at the top
        // of a page that is longer than a viewport.
        cut.WaitForAssertion(() =>
        {
            var field = cut.Find("#cfg-prefix").ParentElement!;
            field.QuerySelector(".field-error")!.TextContent.Should()
                .Contain("Enter the prefix every extension name should start with");
        });
    }

    [Fact]
    public void The_prefix_value_is_only_asked_for_when_a_prefix_is_used()
    {
        var cut = _ctx.Render<AdminTemplateDefaults>();
        cut.WaitForState(() => cut.FindAll("#cfg-prefix-mode").Any(), TimeSpan.FromSeconds(5));

        // PerWorkspace is the default, so the value field starts out shown.
        cut.FindAll("#cfg-prefix").Should().NotBeEmpty();

        cut.Find("#cfg-prefix-mode").Change(nameof(ExtensionPrefixMode.Hidden));
        cut.WaitForAssertion(() => cut.FindAll("#cfg-prefix").Should().BeEmpty(
            "an organisation that uses no prefix has no value to give"));
    }

    [Fact]
    public void Form_renders_html_validation_attributes_matching_the_server_rules()
    {
        var cut = _ctx.Render<AdminTemplateDefaults>();

        cut.WaitForState(() => cut.FindAll("#cfg-publisher").Any(), TimeSpan.FromSeconds(5));

        cut.Find("#cfg-publisher").HasAttribute("required").Should().BeTrue(
            "OrganizationConfigService.SaveSettingsAsync rejects empty publisher; "
            + "the form must surface that to the user");

        var from = cut.Find("#cfg-from");
        from.GetAttribute("type").Should().Be("number");
        from.GetAttribute("min").Should().Be("1");
        from.HasAttribute("required").Should().BeTrue();
    }
}
