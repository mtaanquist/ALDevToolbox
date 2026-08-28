using Microsoft.AspNetCore.DataProtection;
using System.Text.RegularExpressions;
using ALDevToolbox.Components.Pages;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Smoke test for <c>/templates/workspace</c>. The headline assertion is the
/// HTML <c>pattern</c> attribute on the WorkspaceName input matching
/// <c>GenerationService.WorkspaceNameRegex</c> byte for byte — CLAUDE.md
/// §"Always have the end user in mind" requires the client-side rule to
/// mirror the server source of truth. Three-state loading / empty /
/// populated covered as well.
/// </summary>
public sealed class NewWorkspaceTests : IDisposable
{
    /// <summary>
    /// Compiled-time copy of the regex GenerationService uses. The test pins
    /// both that this matches the server's pattern and that the HTML
    /// attribute matches this. If GenerationService.WorkspaceNameRegex
    /// changes without updating the form, both this constant and the test
    /// flip — the failure points straight at the drift.
    /// </summary>
    private const string ServerWorkspaceNameRegex = @"^[A-Za-z][A-Za-z0-9 ]*$";

    private readonly TestDb _db = new();
    private readonly TestContext _ctx = new();

    public NewWorkspaceTests()
    {
        var auth = _ctx.AddTestAuthorization();
        auth.SetAuthorized("tester@example.com");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString));
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddSingleton<IMemoryCache>(new MemoryCache(Options.Create(new MemoryCacheOptions())));
        _ctx.Services.AddScoped<FolderTreeHydrator>();
        _ctx.Services.AddScoped<TemplateService>();
        _ctx.Services.AddScoped<ApplicationVersionService>();
        _ctx.Services.AddScoped<OrganizationConfigService>();
        // The generator pages validate against the real service before they
        // let their native POST through (#546), so it has to be resolvable
        // here or the page cannot even render.
        _ctx.Services.AddScoped<WorkspaceConfigService>();
        _ctx.Services.AddSingleton<ALDevToolbox.Services.Generation.MustacheRenderer>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.Generation.WorkspaceZipBuilder>();
        _ctx.Services.AddScoped<GenerationService>();
        _ctx.Services.AddDataProtection();
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
    public void Server_side_workspace_name_regex_matches_the_compiled_constant_used_by_this_test()
    {
        // Reaching through reflection to pin GenerationService's actual regex
        // would be brittle (private static, RegexOptions.Compiled); the
        // contract is "the source string is identical". If GenerationService
        // changes the pattern, that file's tests will flip — and developers
        // updating the form must also update ServerWorkspaceNameRegex here
        // so the form-vs-server parity assertion below remains meaningful.
        var serverSource = typeof(GenerationService).GetField(
            "WorkspaceNameRegex",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null) as Regex;

        serverSource.Should().NotBeNull();
        serverSource!.ToString().Should().Be(ServerWorkspaceNameRegex,
            "this constant is the test's anchor for the form-vs-server parity check — "
            + "if GenerationService changes the regex, update ServerWorkspaceNameRegex too");
    }

    [Fact]
    public async Task Workspace_name_input_pattern_attribute_matches_the_server_regex()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default());
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.RenderComponent<NewWorkspace>();

        cut.WaitForAssertion(() =>
        {
            var input = cut.Find("input[name='WorkspaceName']");
            input.GetAttribute("pattern").Should().Be(ServerWorkspaceNameRegex,
                "CLAUDE.md §\"Always have the end user in mind\": the HTML pattern= "
                + "must mirror GenerationService.WorkspaceNameRegex — keep the two in sync");
            input.HasAttribute("required").Should().BeTrue(
                "the server rejects null/whitespace; the form must surface that to the user");
        });
    }

    [Fact]
    public void Empty_template_set_renders_the_recovery_copy_pointing_at_admin()
    {
        var cut = _ctx.RenderComponent<NewWorkspace>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".empty-state__title").TextContent.Trim()
                .Should().Be("No workspace templates yet");
            cut.Find(".empty-state__action").GetAttribute("href").Should().Be("/admin/templates",
                "CLAUDE.md §\"three states\" rule — the empty state must tell the user "
                + "how to recover and give them the button to do it");
            cut.FindAll("form").Should().BeEmpty(
                "the form is gated by templates being available — rendering it with "
                + "an empty dropdown would hide the actual problem");
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

        var cut = _ctx.RenderComponent<NewWorkspace>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("form[action='/generate/workspace']").Should().NotBeNull();
            cut.FindAll("button.btn--primary").Should().HaveCount(1,
                "CLAUDE.md §\"Visual hierarchy\": the Generate button is the only "
                + "primary action on the page");
        });
    }

    /// <summary>
    /// The handoff between the page's validation and generate.js. The page
    /// cancels every submit and posts the form itself once the plan is clean
    /// (#546), which only works if two things hold: the form carries the id
    /// <c>aldtGenerate.submit</c> looks up, and it does <em>not</em> carry
    /// <c>data-loading-form</c> — that listener would start the spinner on a
    /// submit the page is about to cancel, leaving it stuck for 30 seconds on
    /// every validation error. Neither is visible in a screenshot and neither
    /// breaks the build.
    /// </summary>
    [Fact]
    public async Task The_form_hands_off_to_generate_js_rather_than_posting_itself()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default(key: "runtime-15"));
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.RenderComponent<NewWorkspace>();

        cut.WaitForAssertion(() =>
        {
            var form = cut.Find("form[action='/generate/workspace']");
            form.Id.Should().Be("gen-workspace-form",
                "generate.js posts the form by this id once validation passes");
            form.HasAttribute("data-loading-form").Should().BeFalse(
                "the listener that attribute binds would start the spinner on a "
                + "submit the page cancels, and nothing would ever clear it");
        });
    }
}
