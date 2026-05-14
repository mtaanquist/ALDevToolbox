using ALDevToolbox.Components.Pages.Admin;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Smoke test for the application-version catalogue editor. Same shape as
/// AdminCatalogTests — empty state + populated state — plus the key
/// <c>pattern=</c> mirror of the server's "lowercase letters, digits,
/// hyphens" rule.
/// </summary>
public sealed class AdminApplicationVersionsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly TestContext _ctx = new();

    public AdminApplicationVersionsTests()
    {
        var auth = _ctx.AddTestAuthorization();
        auth.SetAuthorized("admin@example.com");
        auth.SetRoles("Admin");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString));
        _ctx.Services.AddScoped<ApplicationVersionService>();
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
    public void Empty_catalogue_renders_the_recovery_message_naming_the_add_button()
    {
        var cut = _ctx.RenderComponent<AdminApplicationVersions>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No application versions");
            cut.Markup.Should().Contain("Add entry");
        });
    }

    [Fact]
    public async Task Populated_catalogue_renders_one_row_per_entry_with_a_kebab_case_key_pattern()
    {
        await using (var seed = _db.NewContext())
        {
            seed.ApplicationVersions.Add(new ApplicationVersion
            {
                OrganizationId = TestDb.DefaultOrgId,
                Key = "bc-2026-w1",
                Name = "Business Central 2026 Release Wave 1",
                Application = "28.0.0.0",
                Runtime = "15.2",
                Ordering = 0,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            seed.ApplicationVersions.Add(new ApplicationVersion
            {
                OrganizationId = TestDb.DefaultOrgId,
                Key = "bc-2025-w2",
                Name = "Business Central 2025 Release Wave 2",
                Application = "27.0.0.0",
                Runtime = "15.0",
                Ordering = 1,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.RenderComponent<AdminApplicationVersions>();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("div.folder-editor__row");
            rows.Should().HaveCount(2);

            var keyInput = cut.Find("div.folder-editor__path input[type=text]");
            keyInput.HasAttribute("pattern").Should().BeTrue();
            keyInput.GetAttribute("pattern").Should().Be("[a-z0-9-]+",
                "the HTML pattern= mirrors the server's kebab-case rule on the Key column");
        });
    }
}
