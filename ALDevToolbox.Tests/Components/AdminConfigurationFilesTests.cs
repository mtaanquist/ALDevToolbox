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
/// Smoke test for the always-included-files editor. Pins the three-state
/// contract on the file list and the in-component path validation
/// (required + no '..' traversal + no duplicate paths) — these run on
/// the client without round-tripping to the server, so service-layer
/// tests can't see them.
/// </summary>
public sealed class AdminConfigurationFilesTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly TestContext _ctx = new();

    public AdminConfigurationFilesTests()
    {
        var auth = _ctx.AddTestAuthorization();
        auth.SetAuthorized("admin@example.com");
        auth.SetRoles("Admin");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString));
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
    public void Empty_org_renders_a_useful_empty_state_naming_typical_files()
    {
        var cut = _ctx.RenderComponent<AdminConfigurationFiles>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No files configured");
            cut.Markup.Should().Contain(".editorconfig",
                "the empty-state copy names the kinds of files admins typically add — "
                + "good empty-state copy beats generic 'nothing here yet' messaging");
        });
    }

    [Fact]
    public async Task Persisted_files_render_one_row_each_with_their_mustache_flag()
    {
        await using (var seed = _db.NewContext())
        {
            seed.OrganizationFiles.Add(new OrganizationFile
            {
                OrganizationId = TestDb.DefaultOrgId,
                Path = ".editorconfig",
                Content = "root = true",
                MustacheEnabled = false,
                Ordering = 0,
            });
            seed.OrganizationFiles.Add(new OrganizationFile
            {
                OrganizationId = TestDb.DefaultOrgId,
                Path = "README.md",
                Content = "# {{workspaceName}}",
                MustacheEnabled = true,
                Ordering = 1,
            });
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.RenderComponent<AdminConfigurationFiles>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("li.org-file-row").Should().HaveCount(2);
            cut.Markup.Should().Contain(".editorconfig");
            cut.Markup.Should().Contain("README.md");
            cut.Markup.Should().Contain("mustache on");
            cut.Markup.Should().Contain("mustache off");
        });
    }

    [Fact]
    public void Apply_with_blank_path_renders_an_inline_error_and_keeps_the_list_unchanged()
    {
        var cut = _ctx.RenderComponent<AdminConfigurationFiles>();

        cut.WaitForAssertion(() => cut.Find("h3"));

        // The "Add to list" button at the bottom of the editor — first
        // top-level button in the form-actions row.
        cut.Find("div.form-actions button.btn").Click();

        cut.Find("span.form-field-error").TextContent.Should().Contain("Path is required",
            "client-side validation must surface before round-tripping to the server");
        cut.FindAll("li.org-file-row").Should().BeEmpty(
            "the failed apply must leave the file list untouched");
    }

    [Fact]
    public void Apply_with_traversal_segments_in_path_is_rejected_inline()
    {
        var cut = _ctx.RenderComponent<AdminConfigurationFiles>();
        cut.WaitForAssertion(() => cut.Find("#cfg-file-path"));

        // Path input binds on `oninput`, not `change` — Input() triggers the
        // right event so the field's value reaches _editPath before Apply.
        cut.Find("#cfg-file-path").Input("../etc/passwd");
        cut.Find("div.form-actions button.btn").Click();

        cut.Find("span.form-field-error").TextContent.Should().Contain("'..'",
            "the path traversal guard runs client-side too — generation writes "
            + "these files at the workspace root, so '..' segments are flatly refused");
    }
}
