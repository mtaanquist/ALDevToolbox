using ALDevToolbox.Components.Pages.Admin.Administration;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The two default delivery window rows on Administration → Business Central (issue
/// #962). The named user is a consultant who wants every new customer's production
/// environment kept out of daytime installs without having to remember to set it.
/// </summary>
public sealed class AdminBusinessCentralDefaultWindowTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    private const int AdminUserId = 9620;

    public AdminBusinessCentralDefaultWindowTests()
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
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<ProjectConnectionService>();
        _ctx.Services.AddScoped(sp => _db.NewOrganizationAdminService(sp.GetRequiredService<AppDbContext>()));
        _ctx.Services.AddSingleton<IBcAdminClient>(new UnreachableAdminClient());
        _ctx.Services.AddSingleton<IBcAppManagementClient>(new UnreachableAppManagementClient());
        _ctx.Services.AddSingleton(new BcTokenService(
            new UnreachableHttpClientFactory(), NullLogger<BcTokenService>.Instance));
        _ctx.Services.AddSingleton(_db.DataProtectionProvider);
        _ctx.Services.AddSingleton(new BcPanelCache(TimeProvider.System));
        _ctx.Services.AddSingleton(TimeProvider.System);
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

    private async Task<OrganizationSettings?> StoredAsync()
    {
        await using var read = _db.NewContext();
        return await read.OrganizationSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrganizationId == TestDb.DefaultOrgId);
    }

    [Fact]
    public void Both_rows_render_with_the_caption_and_start_at_any_time()
    {
        var cut = _ctx.Render<AdminAdministrationBusinessCentral>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Default delivery window for Production");
            cut.Markup.Should().Contain("Default delivery window for Sandbox");
            cut.Markup.Should().Contain("Applied to environments the first time they appear.");
            cut.Markup.Should().Contain("No window yet — new Production environments accept deliveries at any time.");
            cut.Find("#default-window-production-start").GetAttribute("value").Should().BeNullOrEmpty();
            cut.Find("#default-window-sandbox-end").GetAttribute("value").Should().BeNullOrEmpty();
        });
    }

    [Fact]
    public async Task The_rows_open_on_the_saved_windows()
    {
        await _db.NewOrganizationAdminService(_db.NewContext()).SetDefaultDeliveryWindowsAsync(
            new DefaultDeliveryWindows(new TimeOnly(22, 0), new TimeOnly(6, 0), new TimeOnly(18, 0), new TimeOnly(20, 0)));

        var cut = _ctx.Render<AdminAdministrationBusinessCentral>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("#default-window-production-start").GetAttribute("value").Should().StartWith("22:00");
            cut.Find("#default-window-production-end").GetAttribute("value").Should().StartWith("06:00");
            cut.Find("#default-window-sandbox-start").GetAttribute("value").Should().StartWith("18:00");
        });
    }

    [Fact]
    public async Task Entering_both_times_saves_the_row_and_leaves_the_other_alone()
    {
        var cut = _ctx.Render<AdminAdministrationBusinessCentral>();

        // With seconds, as Blazor's script hands a time input's value to the binder.
        cut.WaitForAssertion(() => cut.Find("#default-window-production-start").Change("22:00:00"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Enter the end time too"));
        (await StoredAsync())?.DefaultDeliveryWindowProductionStart.Should().BeNull("half a window waits for the other half");

        cut.WaitForAssertion(() => cut.Find("#default-window-production-end").Change("06:00:00"));
        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("Saved. New Production environments start with a 22:00 to 06:00 window (overnight)."));

        var row = await StoredAsync();
        row!.DefaultDeliveryWindowProductionStart.Should().Be(new TimeOnly(22, 0));
        row.DefaultDeliveryWindowProductionEnd.Should().Be(new TimeOnly(6, 0));
        row.DefaultDeliveryWindowSandboxStart.Should().BeNull();
    }

    [Fact]
    public async Task Any_time_clears_a_saved_window()
    {
        await _db.NewOrganizationAdminService(_db.NewContext()).SetDefaultDeliveryWindowsAsync(
            new DefaultDeliveryWindows(new TimeOnly(22, 0), new TimeOnly(6, 0), new TimeOnly(18, 0), new TimeOnly(20, 0)));
        var cut = _ctx.Render<AdminAdministrationBusinessCentral>();

        cut.WaitForAssertion(() => cut.FindAll("button").First(b => b.TextContent == "Any time").Click());
        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("Saved. New Production environments start with no window"));

        var row = await StoredAsync();
        row!.DefaultDeliveryWindowProductionStart.Should().BeNull();
        row.DefaultDeliveryWindowProductionEnd.Should().BeNull();
        row.DefaultDeliveryWindowSandboxStart.Should().Be(new TimeOnly(18, 0));
    }
}
