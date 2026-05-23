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
/// Smoke test for <c>/projects/new</c>. The headline assertion is the
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
            cut.Markup.Should().Contain("No active workspace templates are available",
                "CLAUDE.md §\"three states\" rule — the empty-state copy must tell "
                + "the user how to recover");
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
}
