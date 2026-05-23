using System.Text.RegularExpressions;
using ALDevToolbox.Components.Pages;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
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
/// Smoke test for <c>/projects/extension</c>. Pins the second of the two
/// form/server-regex parity contracts (the ExtensionName one) plus the
/// three-state contract. The sibling-extension hidden-input rendering
/// (<c>_workspaceContext is not null</c>) is a separate branch covered by
/// its own test.
/// </summary>
public sealed class NewExtensionTests : IDisposable
{
    private const string ServerExtensionNameRegex = @"^[A-Za-z][A-Za-z0-9]*$";

    private readonly TestDb _db = new();
    private readonly TestContext _ctx = new();

    public NewExtensionTests()
    {
        var auth = _ctx.AddTestAuthorization();
        auth.SetAuthorized("tester@example.com");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString));
        _ctx.Services.AddSingleton<IMemoryCache>(new MemoryCache(Options.Create(new MemoryCacheOptions())));
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddScoped<FolderTreeHydrator>();
        _ctx.Services.AddScoped<TemplateService>();
        _ctx.Services.AddScoped<ApplicationVersionService>();
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddScoped<WorkspaceConfigService>();
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
    public void Server_side_extension_name_regex_matches_the_compiled_constant_used_by_this_test()
    {
        var serverSource = typeof(GenerationService).GetField(
            "ExtensionNameRegex",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null) as Regex;

        serverSource.Should().NotBeNull();
        serverSource!.ToString().Should().Be(ServerExtensionNameRegex,
            "if GenerationService.ExtensionNameRegex changes, update ServerExtensionNameRegex here "
            + "so the form-vs-server parity assertion remains meaningful");
    }

    [Fact]
    public async Task Extension_name_input_pattern_attribute_matches_the_server_regex()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default());
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.RenderComponent<NewExtension>();

        cut.WaitForAssertion(() =>
        {
            var input = cut.Find("input[name='ExtensionName']");
            input.GetAttribute("pattern").Should().Be(ServerExtensionNameRegex,
                "the HTML pattern= must mirror GenerationService.ExtensionNameRegex");
            input.HasAttribute("required").Should().BeTrue();
        });
    }

    [Fact]
    public void Empty_template_set_renders_the_recovery_copy_pointing_at_admin()
    {
        var cut = _ctx.RenderComponent<NewExtension>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No active workspace templates are available");
            cut.FindAll("form").Should().BeEmpty();
        });
    }

    [Fact]
    public async Task Populated_template_set_renders_the_form_with_a_single_primary_generate_button()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default(key: "runtime-15"));
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.RenderComponent<NewExtension>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("form[action='/generate/extension']").Should().NotBeNull();
            cut.FindAll("button.btn--primary").Should().HaveCount(1,
                "CLAUDE.md §\"Visual hierarchy\": the Generate button is the only "
                + "primary action on the page");
        });
    }

    [Fact]
    public async Task Publisher_input_is_absent_so_org_defaults_drive_the_value()
    {
        // The Publisher input was removed from the form: there's exactly one
        // publisher per org (curated under /admin/configuration/defaults) and
        // /generate/extension resolves it server-side from
        // OrganizationSettings.DefaultPublisher. Pinning the absence here so
        // a future regression doesn't quietly reintroduce the typo surface.
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default());
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.RenderComponent<NewExtension>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("input[name='Publisher']").Should().BeEmpty(
                "Publisher is org-level configuration, not a per-extension form field — "
                + "the endpoint reads OrganizationSettings.DefaultPublisher instead.");
        });
    }
}
