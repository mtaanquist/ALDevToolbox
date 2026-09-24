using ALDevToolbox.Components.Pages.Admin.Administration;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The display time zone field on Administration → Identity (issue #942): it
/// opens on the saved zone, and picking one saves it through the service.
/// </summary>
public sealed class AdminIdentityTimeZoneTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    private const int AdminUserId = 9420;

    public AdminIdentityTimeZoneTests()
    {
        using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                Id = AdminUserId,
                OrganizationId = TestDb.DefaultOrgId,
                Email = "admin@example.com",
                PasswordHash = "x",
                DisplayName = "Admin",
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
            });
            seed.SaveChanges();
        }
        _db.OrgContext.CurrentUserId = AdminUserId;

        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("admin@example.com");
        auth.SetRoles("Admin");
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddSingleton(_db.NewContextFactory());
        _ctx.Services.AddScoped(sp => _db.NewOrganizationAdminService(sp.GetRequiredService<AppDbContext>()));
        _ctx.Services.AddScoped(sp => _db.NewOrganizationBrandingService(sp.GetRequiredService<AppDbContext>()));
        _ctx.Services.AddScoped<DisplayTimeZone>();
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

    private async Task<string?> StoredZoneAsync()
    {
        await using var read = _db.NewContext();
        return await read.OrganizationSettings.AsNoTracking()
            .Where(s => s.OrganizationId == TestDb.DefaultOrgId)
            .Select(s => s.DisplayTimeZoneId)
            .FirstOrDefaultAsync();
    }

    [Fact]
    public void With_nothing_saved_the_select_shows_UTC_as_the_default()
    {
        var cut = _ctx.Render<AdminAdministrationIdentity>();

        cut.WaitForAssertion(() =>
        {
            var select = cut.Find("#org-time-zone");
            select.GetAttribute("value").Should().Be(string.Empty);
            select.QuerySelector("option")!.TextContent.Should().Be("UTC (default)");
            cut.Markup.Should().Contain("Times in AL Workbench are shown in this zone. Hover a time for the UTC instant.");
            select.QuerySelectorAll("option").Select(o => o.GetAttribute("value"))
                .Should().Contain("Europe/Copenhagen");
        });
    }

    [Fact]
    public async Task The_select_opens_on_the_saved_zone()
    {
        await _db.NewOrganizationAdminService(_db.NewContext()).SetDisplayTimeZoneAsync("Europe/Copenhagen");

        var cut = _ctx.Render<AdminAdministrationIdentity>();

        cut.WaitForAssertion(() =>
            cut.Find("#org-time-zone").GetAttribute("value").Should().Be("Europe/Copenhagen"));
    }

    [Fact]
    public async Task Picking_a_zone_saves_it()
    {
        var cut = _ctx.Render<AdminAdministrationIdentity>();

        cut.WaitForAssertion(() => cut.Find("#org-time-zone").Change("Europe/Copenhagen"));
        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("Times are shown in Europe/Copenhagen from the next page you open."));

        (await StoredZoneAsync()).Should().Be("Europe/Copenhagen");
    }
}
