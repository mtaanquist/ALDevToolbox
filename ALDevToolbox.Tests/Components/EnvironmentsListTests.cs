using ALDevToolbox.Components.Pages.Environments;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The cross-solution Environments list (design archetype 2a). The named user is a
/// BC consultant checking every customer environment they can reach in one table.
///
/// <para>The rule most worth pinning is which timestamp "Last checked" reports.
/// A row carries two, stamped by different reads: the environment itself, and the
/// next-update mirror. A tenant can answer one and refuse the other, so reading the
/// wrong one reports an environment as never checked when only its updates were
/// unreadable.</para>
/// </summary>
public sealed class EnvironmentsListTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private const int OwnerUserId = 9800;

    public EnvironmentsListTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("owner@example.com");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<UpgradeFleetService>();
        _ctx.Services.AddSingleton(new EnvironmentRefreshQueue());
        // The row menu's upload goes through the connection service.
        _ctx.Services.AddScoped<ProjectConnectionService>();
        _ctx.Services.AddSingleton<IBcAdminClient>(new UnreachableAdminClient());
        _ctx.Services.AddSingleton<IBcAppManagementClient>(new UnreachableAppManagementClient());
        _ctx.Services.AddSingleton(new BcTokenService(
            new UnreachableHttpClientFactory(), NullLogger<BcTokenService>.Instance));
        _ctx.Services.AddSingleton(_db.DataProtectionProvider);
        _ctx.Services.AddSingleton(new BcPanelCache(TimeProvider.System));
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(
            new Microsoft.AspNetCore.Http.HttpContextAccessor());
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));

        using var seed = _db.NewContext();
        seed.Users.Add(new User
        {
            Id = OwnerUserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "owner@example.com",
            PasswordHash = "x",
            DisplayName = "Owner",
            Role = UserRole.Editor,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        seed.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    private async Task<int> SeedSolutionAsync(string name)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            CreatedByUserId = OwnerUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private async Task SeedEnvironmentAsync(
        int projectId, string name, string type, string? status,
        DateTime environmentFetchedAt, DateTime? updatesFetchedAt)
    {
        await using var ctx = _db.NewContext();
        ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = name,
            Type = type,
            Status = status,
            Version = "28.2",
            FetchedAt = environmentFetchedAt,
            BcNextUpdateFetchedAt = updatesFetchedAt,
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task An_org_with_no_connected_solution_gets_the_first_run_empty_state()
    {
        await SeedSolutionAsync("CRONUS Denmark");

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() =>
            cut.Find(".empty-state__title").TextContent.Trim()
                .Should().Be("No environments to show yet"));
        // The empty state has to say how to get out of it, not just that it is empty.
        cut.Find(".empty-state__text").TextContent
            .Should().Contain("Business Central connection");
        cut.Find(".empty-state__action a").TextContent.Trim().Should().Be("Go to solutions");
    }

    [Fact]
    public async Task Last_checked_reports_the_environment_read_not_the_update_mirror()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        // Read half an hour ago; its updates have never been readable at all.
        await SeedEnvironmentAsync(id, "Production", "Production", "Active",
            environmentFetchedAt: DateTime.UtcNow.AddMinutes(-30),
            updatesFetchedAt: null);

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(1));
        var lastChecked = cut.FindAll(".data-table tbody tr td")
            .Last(c => !c.ClassList.Contains("data-table__actions")).TextContent.Trim();
        lastChecked.Should().NotBe("never",
            "the environment was read half an hour ago - only its updates were unreadable");
        lastChecked.Should().Contain("minutes ago");
    }

    [Fact]
    public async Task A_row_offers_to_upload_an_app_and_the_dialog_names_that_environment()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "Production", "Production", "Active", now, now);

        var cut = _ctx.Render<EnvironmentsList>();
        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(1));

        cut.WaitForAssertion(() =>
            cut.FindAll("button.menu__item").Single(b => b.TextContent.Trim() == "Upload an app...").Click());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Upload an app to Production, a production environment?"));
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Upload and install")
            .HasAttribute("disabled").Should().BeTrue("nothing has been chosen yet");
    }

    [Fact]
    public async Task An_environment_part_way_through_an_update_does_not_need_attention()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "Production", "Production", "Updating", now, now);
        await SeedEnvironmentAsync(id, "UAT", "Sandbox", "Suspended", now, now);
        await SeedEnvironmentAsync(id, "Test", "Sandbox", "Active", now, now);

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(3));
        var tabs = cut.FindAll(".pill-tab").Select(t => t.TextContent.Trim()).ToList();
        tabs.Should().Contain(t => t.StartsWith("Needs attention"));
        // Suspended counts; mid-update is the system working, so it must not.
        tabs.First(t => t.StartsWith("Needs attention")).Should().EndWith("1");
    }

    /// <summary>
    /// The design gives a table row no status column: the state is the edge bar plus a
    /// glyph. Four states share two glyphs and a title does not exist on touch, so the
    /// word must still be on screen for anything that is not plainly running - under
    /// Next update, where the designed sheet puts it.
    /// </summary>
    [Fact]
    public async Task State_is_a_named_glyph_and_the_word_stays_on_screen_unless_running()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "Production", "Production", "Active", now, now);
        await SeedEnvironmentAsync(id, "UAT", "Sandbox", "Suspended", now, now);

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(2));
        var rows = cut.FindAll(".data-table tbody tr");
        var running = rows.Single(r => r.TextContent.Contains("Production"));
        var suspended = rows.Single(r => r.TextContent.Contains("UAT"));

        foreach (var row in rows)
        {
            var glyph = row.QuerySelector("td.data-table__col-state > .data-table__state--icon")!;
            glyph.GetAttribute("role").Should().Be("img");
            glyph.GetAttribute("aria-label").Should().NotBeNullOrWhiteSpace().And.Be(glyph.GetAttribute("title"));
            glyph.TextContent.Trim().Should().BeEmpty("the state cell is a glyph, not a label column");
        }

        suspended.QuerySelectorAll(".cell-stack__sub").Last().TextContent.Should().Be("Suspended by Microsoft");
        running.QuerySelectorAll(".cell-stack__sub").Last().TextContent.Should().Be("Nothing scheduled");
        running.TextContent.Should().NotContain("Running", "a healthy row spends no words on its state");
    }

    // ── Storage: the environment's size, the customer's tenant against its allowance ──

    private async Task SetStorageAsync(int projectId, long quotaKb, params (string Environment, long Kb)[] sizes)
    {
        await using var ctx = _db.NewContext();
        var project = await ctx.OeProjects.SingleAsync(p => p.Id == projectId);
        project.BcStorageQuotaKb = quotaKb;
        project.BcStorageFetchedAt = DateTime.UtcNow;
        foreach (var (environment, kb) in sizes)
        {
            (await ctx.OeProjectEnvironments.SingleAsync(e => e.ProjectId == projectId && e.Name == environment)).BcDatabaseKb = kb;
        }
        await ctx.SaveChangesAsync();
    }

    private const long Gb = 1024 * 1024;

    [Fact]
    public async Task Each_row_shows_its_own_size_and_the_customers_tenant_against_its_allowance()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        await SeedEnvironmentAsync(id, "Production", "Production", "Active", DateTime.UtcNow, DateTime.UtcNow);
        await SeedEnvironmentAsync(id, "Sandbox", "Sandbox", "Active", DateTime.UtcNow, DateTime.UtcNow);
        await SetStorageAsync(id, 80 * Gb, ("Production", 50 * Gb), ("Sandbox", 18 * Gb));

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() =>
        {
            var cells = cut.FindAll(".env-storage");
            cells.Select(c => c.QuerySelector(".cell-stack__main")!.TextContent).Should().BeEquivalentTo(["50.0 GB", "18.0 GB"]);
            cells.Should().AllSatisfy(c =>
            {
                c.QuerySelector(".cell-stack__sub")!.TextContent.Should().Be("Customer at 85% of 80.0 GB",
                    "the allowance is the tenant's, so both rows carry the same 68 of 80");
                c.QuerySelector("progress")!.ClassList.Should().Contain("env-storage__bar--warn");
            });
        });
    }

    [Fact]
    public async Task A_customer_over_their_allowance_gets_a_full_red_bar_and_is_told_so_in_words()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        await SeedEnvironmentAsync(id, "Production", "Production", "Active", DateTime.UtcNow, DateTime.UtcNow);
        await SetStorageAsync(id, 80 * Gb, ("Production", 92 * Gb));

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() =>
        {
            var cell = cut.Find(".env-storage");
            cell.QuerySelector(".cell-stack__sub")!.TextContent.Should().Be("Customer at 115% of 80.0 GB - over its allowance");
            var bar = cell.QuerySelector("progress")!;
            bar.ClassList.Should().Contain("env-storage__bar--danger");
            bar.GetAttribute("value").Should().Be("1", "a bar cannot be more than full; the words carry the rest");
        });
        cut.FindAll(".pill-tab, .seg__btn, [role=tab]").Single(t => t.TextContent.Contains("Needs attention"))
            .TextContent.Should().Contain("1", "over the allowance is a conversation somebody has to have");
    }

    [Fact]
    public async Task Storage_nobody_has_read_yet_is_a_dash_and_a_roomy_tenant_gets_no_colour()
    {
        var unread = await SeedSolutionAsync("CRONUS Norway");
        await SeedEnvironmentAsync(unread, "Production", "Production", "Active", DateTime.UtcNow, DateTime.UtcNow);
        var roomy = await SeedSolutionAsync("CRONUS Sweden");
        await SeedEnvironmentAsync(roomy, "Production", "Production", "Active", DateTime.UtcNow, DateTime.UtcNow);
        await SetStorageAsync(roomy, 80 * Gb, ("Production", 800 * 1024));

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() =>
        {
            var cell = cut.FindAll(".env-storage").Should().ContainSingle().Subject;
            cell.QuerySelector(".cell-stack__main")!.TextContent.Should().Be("800 MB");
            cell.QuerySelector("progress")!.ClassList.Should().NotContain(c => c.StartsWith("env-storage__bar--"));
        });
    }
}
