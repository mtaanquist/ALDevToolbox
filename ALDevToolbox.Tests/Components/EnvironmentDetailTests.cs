using ALDevToolbox.Components.Pages.Environments;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// One environment's own page (PageEnvironmentDetail.dc.html, issue #809). The named
/// user is an ops engineer who would otherwise open the customer's admin centre.
///
/// <para>Two readings share the page and the tests keep them apart. The head, the meta
/// row and the Updates card are our own mirror: anyone who can see the solution gets
/// them. Apps and Environment settings are live, with the customer's credentials, and
/// are for people who manage the solution. The live half is served here from the panel
/// cache, so Business Central is never reached - the doubles throw if it is.</para>
/// </summary>
public sealed class EnvironmentDetailTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly BcPanelCache _panels = new(TimeProvider.System);
    private const int OwnerUserId = 9870;
    private const int ColleagueUserId = 9871;
    private static readonly Guid TenantId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public EnvironmentDetailTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("owner@example.com");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<UpgradeFleetService>();
        _ctx.Services.AddScoped<UpgradeActionService>();
        _ctx.Services.AddScoped<ProjectConnectionService>();
        _ctx.Services.AddSingleton<IBcAdminClient>(new UnreachableAdminClient());
        _ctx.Services.AddSingleton<IBcAppManagementClient>(new UnreachableAppManagementClient());
        _ctx.Services.AddSingleton(new BcTokenService(
            new UnreachableHttpClientFactory(), NullLogger<BcTokenService>.Instance));
        _ctx.Services.AddSingleton(_db.DataProtectionProvider);
        _ctx.Services.AddSingleton(_panels);
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.Services.AddSingleton(new EnvironmentRefreshQueue());
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        using var seed = _db.NewContext();
        seed.Users.AddRange(
            NewUser(OwnerUserId, "owner@example.com"),
            NewUser(ColleagueUserId, "colleague@example.com"));
        seed.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    private static User NewUser(int id, string email) => new()
    {
        Id = id,
        OrganizationId = TestDb.DefaultOrgId,
        Email = email,
        PasswordHash = "x",
        DisplayName = email,
        Role = UserRole.Editor,
        Status = UserStatus.Active,
        CreatedAt = DateTime.UtcNow,
    };

    private async Task<(int ProjectId, int EnvironmentId)> SeedAsync(
        ProjectVisibility visibility = ProjectVisibility.Public)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS Denmark",
            Visibility = visibility,
            BcTenantId = TenantId,
            BcTimeZone = "Europe/Copenhagen",
            CreatedByUserId = OwnerUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = project.Id,
            Name = "Production",
            Type = "Production",
            ApplicationFamily = "BusinessCentral",
            Status = "Active",
            Version = "28.2.41125.0",
            CountryCode = "DK",
            LocationName = "West Europe",
            FetchedAt = DateTime.UtcNow,
            UpdateWindowStart = new TimeOnly(22, 0),
            UpdateWindowEnd = new TimeOnly(4, 0),
            BcNextUpdateVersion = "28.3",
            BcNextUpdateDate = new DateTime(2026, 10, 12, 2, 0, 0, DateTimeKind.Utc),
            BcNextUpdateLatestDate = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc),
            BcNextUpdateFetchedAt = DateTime.UtcNow,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();
        return (project.Id, env.Id);
    }

    private static readonly Guid CoreId = Guid.NewGuid();

    private static BcEnvironmentPanel Panel() => new(
        "Production",
        new HashSet<Guid>(),
        [
            new BcInstalledApp(Guid.NewGuid(), "CRONUS Sales Extension", "CRONUS International", "1.4.0.882", "Installed", "tenant", true, null, string.Empty),
            new BcInstalledApp(CoreId, "Continia Core", "Continia Software", "28.4.1.359421", "Installed", "global", true, null, string.Empty),
            new BcInstalledApp(Guid.NewGuid(), "Payables Agent", "Microsoft", "28.2.50931.53916", "Installed", "global", false, null, string.Empty),
        ],
        null,
        [
            // Blocked first on purpose: the page has to put the ready one above it.
            new BcAvailableAppUpdate(Guid.NewGuid(), "Continia Connector App", "Continia Software", "28.5.0.360827",
                [new BcAppUpdateRequirement(CoreId, "Continia Core", "Continia Software", "28.5.0.363410", "update")]),
            new BcAvailableAppUpdate(CoreId, "Continia Core", "Continia Software", "28.5.0.363410", []),
        ],
        null,
        [],
        null,
        [
            new BcEnvironmentUpdate("28.3", true, true, "scheduled", "GA", new DateTimeOffset(2026, 10, 12, 2, 0, 0, TimeSpan.Zero), null, false, "Active", null, null),
            new BcEnvironmentUpdate("28.4", true, false, "available", "GA", null, null, false, "Active", null, null),
        ],
        null,
        DateTime.UtcNow);

    private IRenderedComponent<EnvironmentDetail> Render(int environmentId)
    {
        var cut = _ctx.Render<EnvironmentDetail>(p => p.Add(c => c.Id, environmentId));
        cut.WaitForAssertion(() => cut.FindAll(".loading-block").Should().BeEmpty());
        return cut;
    }

    [Fact]
    public async Task The_head_meta_row_and_updates_card_come_from_our_own_mirror()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());

        var cut = Render(envId);

        cut.Find("h1.detail-head__title").TextContent.Should().Be("Production");
        cut.Find(".detail-head__title-row .status-pill").ClassList.Should().Contain("status-pill--success");
        cut.Find(".detail-head__title-row .tag").TextContent.Should().Be("Production");
        cut.Find(".page-head__sub").TextContent.Trim().Should()
            .Be("Business Central environment in CRONUS Denmark - BC version 28.2 - West Europe");
        cut.FindAll(".page-head__crumbs a").Select(a => a.GetAttribute("href")).Should()
            .Equal("/solutions", $"/solutions/{projectId}", "/environments");

        cut.FindAll(".meta-row .meta-item__label").Select(l => l.TextContent).Should()
            .Equal("Type", "BC version", "Country", "Delivery window", "Next update", "Apps installed");
        var meta = cut.FindAll(".meta-row .meta-item__value").Select(v => v.TextContent.Trim()).ToList();
        meta[1].Should().Be("28.2.41125.0");
        meta[2].Should().Be("Denmark (DK)");
        meta[3].Should().Be("22:00-04:00 (Copenhagen)");
        meta[4].Should().Be("12 Oct 2026");
        meta[5].Should().Be("3");

        cut.FindAll(".kv-grid .meta-item__label").Select(l => l.TextContent).Should().Equal(
            "Current version", "Next update version", "Scheduled for", "Latest possible date",
            "Our delivery window", "BC update window");

        var open = cut.Find(".page-head__actions a");
        open.GetAttribute("href").Should().Be($"https://businesscentral.dynamics.com/{TenantId:D}/Production");
    }

    [Fact]
    public async Task Waiting_updates_put_the_ready_ones_first_and_name_what_the_rest_wait_for()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());

        var cut = Render(envId);

        var rows = cut.FindAll("table.u-compact tbody tr");
        rows.Should().HaveCount(2);
        rows[0].ClassList.Should().Contain("is-published");
        rows[0].QuerySelector(".status-pill")!.TextContent.Should().Be("Ready");
        rows[0].Children[1].TextContent.Should().Be("Continia Core");
        rows[1].ClassList.Should().Contain("is-queued");
        rows[1].QuerySelector(".status-pill")!.TextContent.Should().Be("Waits for 1");
        rows[1].QuerySelectorAll(".tag").Select(t => t.TextContent).Should().Equal("Continia Core");

        // Both rows can be updated; the waiting one says up front that it moves others.
        rows[0].QuerySelector(".data-table__actions button")!.GetAttribute("aria-label")
            .Should().Be("Update Continia Core to 28.5.0.363410");
        rows[1].QuerySelector(".data-table__actions button")!.GetAttribute("aria-label")
            .Should().EndWith("along with the 1 it waits for");
    }

    [Fact]
    public async Task Updating_a_waiting_app_lists_what_moves_with_it_before_anything_is_sent()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());
        var cut = Render(envId);

        cut.FindAll("table.u-compact tbody tr")[1].QuerySelector(".data-table__actions button")!.Click();

        cut.WaitForAssertion(() =>
            cut.FindAll(".env-detail__alongside-list li").Select(li => li.Children[0].TextContent)
                .Should().Equal("Continia Core"));
        cut.Markup.Should().Contain("and 1 other app it waits for");
    }

    [Fact]
    public async Task An_app_from_another_company_can_be_uploaded_but_not_before_a_file_is_chosen()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());
        var cut = Render(envId);

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Upload an app").Click();

        cut.WaitForAssertion(() => cut.Find("#upload-app-file").GetAttribute("accept").Should().Be(".app"));
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Upload and install")
            .HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task Installed_apps_say_where_each_came_from_and_the_filter_narrows_them()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());

        var cut = Render(envId);

        string[] Sources() => cut.FindAll("table.data-table:not(.u-compact) tbody tr")
            .Select(r => r.Children[0].TextContent.Trim() + " / " + r.Children[3].TextContent.Trim()).ToArray();

        Sources().Should().Equal(
            "CRONUS Sales Extension / Per-tenant", "Continia Core / AppSource", "Payables Agent / Microsoft");

        cut.Find("input[aria-label='Filter installed apps']").Input("continia");

        // The filter re-renders on its own turn, so the narrowed list is waited for.
        cut.WaitForAssertion(() => Sources().Should().Equal("Continia Core / AppSource"));
    }

    [Fact]
    public async Task The_three_settings_are_there_and_none_of_them_is_a_primary_button()
    {
        var (projectId, envId) = await SeedAsync();
        _panels.Set(projectId, envId, Panel());

        var cut = Render(envId);

        cut.FindAll(".setting-list .setting__name").Select(n => n.TextContent).Should().Equal(
            "AppSource apps update cadence", "Access with Microsoft 365 licences", "Next Business Central update");
        cut.Find(".setting--danger .setting__lock").TextContent.Should().Contain("Writes to the customer's tenant");
        cut.FindAll("#env-version option").Select(o => o.TextContent).Should()
            .Equal("Choose a version...", "28.3 (already next)", "28.4");
        cut.Find(".alert--warn").TextContent.Should().Contain("These change Production in the customer's tenant");
        cut.FindAll(".btn--primary").Should().BeEmpty(
            "nothing on this page is the one thing to do; every action is an outline button");
    }

    [Fact]
    public async Task Someone_who_can_see_the_solution_but_not_manage_it_gets_the_mirror_and_not_the_live_half()
    {
        var (_, envId) = await SeedAsync();
        _db.OrgContext.CurrentUserId = ColleagueUserId;

        var cut = Render(envId);

        cut.Find("h1.detail-head__title").TextContent.Should().Be("Production");
        cut.FindAll(".kv-grid").Should().HaveCount(1);
        cut.Find(".empty-state__title").TextContent.Should()
            .Be("Apps and settings are for people who manage this solution");
        cut.FindAll(".setting-list").Should().BeEmpty();
        cut.FindAll(".freshness button").Should().BeEmpty(
            "Refresh reads the customer's tenant, which this person may not do");
    }

    [Fact]
    public async Task A_solution_with_no_connection_says_so_and_points_at_the_tab_that_fixes_it()
    {
        var (projectId, envId) = await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            var project = await ctx.OeProjects.SingleAsync(p => p.Id == projectId);
            project.BcTenantId = null;
            await ctx.SaveChangesAsync();
        }

        var cut = Render(envId);

        cut.Find(".empty-state__title").TextContent.Should().Be("Couldn't read apps and settings from Business Central");
        cut.Find(".empty-state__action a").GetAttribute("href").Should().Be($"/solutions/{projectId}?tab=bc");
        cut.FindAll(".kv-grid").Should().HaveCount(1, "the mirror does not need the connection");
    }

    [Fact]
    public async Task An_environment_the_person_cannot_see_reads_exactly_like_one_that_does_not_exist()
    {
        var (_, hidden) = await SeedAsync(ProjectVisibility.Private);
        _db.OrgContext.CurrentUserId = ColleagueUserId;

        foreach (var id in new[] { hidden, hidden + 1000 })
        {
            var cut = Render(id);

            cut.Find("h1.empty-state__title").TextContent.Should().Be("We couldn't find that environment");
            cut.FindAll(".detail-head").Should().BeEmpty();
            cut.Markup.Should().NotContain("CRONUS Denmark");
        }
    }
}
