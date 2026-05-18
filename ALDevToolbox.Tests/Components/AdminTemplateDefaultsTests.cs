using ALDevToolbox.Components.Pages.Admin;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
    private readonly TestContext _ctx = new();

    public AdminTemplateDefaultsTests()
    {
        var auth = _ctx.AddTestAuthorization();
        auth.SetAuthorized("admin@example.com");
        auth.SetRoles("Admin");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString));
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddSingleton<IMemoryCache>(new MemoryCache(Options.Create(new MemoryCacheOptions())));
        _ctx.Services.AddScoped<OrganizationConfigService>();
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

        var cut = _ctx.RenderComponent<AdminTemplateDefaults>();

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
    public void Form_renders_html_validation_attributes_matching_the_server_rules()
    {
        var cut = _ctx.RenderComponent<AdminTemplateDefaults>();

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
