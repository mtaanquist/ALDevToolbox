using Microsoft.AspNetCore.Components;
using ALDevToolbox.Components.Pages.Upgrades;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
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
/// The Upgrades fleet table. The named user is a member of the BC upgrade team who
/// schedules platform updates for around a hundred solutions and reads this table
/// across, row by row.
///
/// <para>The page follows the design's actionable-list sheet (PageUpgrades.dc.html):
/// one command bar whose selection commands are disabled until a row is ticked, a
/// glyph for state, and the row's commands - history first - in one trailing menu
/// rather than as buttons inside data cells, which is what #805 got wrong.</para>
/// </summary>
public sealed class UpgradesPageTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private const int AdminUserId = 9850;
    private static readonly Guid TenantId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public UpgradesPageTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("upgrades@example.com");

        // The page asks the browser for the last view picked; by default it has none.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
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
        _ctx.Services.AddSingleton(new BcPanelCache(TimeProvider.System));
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.Services.AddSingleton(new EnvironmentRefreshQueue());
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));

        using var seed = _db.NewContext();
        seed.Users.Add(new User
        {
            Id = AdminUserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "upgrades@example.com",
            PasswordHash = "x",
            DisplayName = "Anna Jensen",
            // An org admin holds the environment-ops grant outright, which is the
            // shortest way to a page that renders its table at all.
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        seed.SaveChanges();
        _db.OrgContext.CurrentUserId = AdminUserId;
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    private async Task SeedOneEnvironmentAsync()
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS Denmark",
            BcTenantId = TenantId,
            CreatedByUserId = AdminUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = project.Id,
            Name = "Production",
            Type = "Production",
            ApplicationFamily = "BusinessCentral",
            Status = "Active",
            Version = "27.5.12345.0",
            FetchedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private IRenderedComponent<UpgradesPage> RenderWithOneRow()
    {
        var cut = _ctx.Render<UpgradesPage>();
        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(1));
        return cut;
    }

    /// <summary>
    /// The page exists to move update dates, and a deleted environment's date cannot be
    /// moved: Business Central offers it no update at all. It is dropped in the fleet
    /// query rather than in the page, so the counts, the checkbox selection and the two
    /// bulk actions all agree without each having to remember.
    /// </summary>
    [Fact]
    public async Task A_deleted_environment_is_not_on_the_upgrades_table_at_all()
    {
        await SeedOneEnvironmentAsync();
        await using (var ctx = _db.NewContext())
        {
            var env = await ctx.OeProjectEnvironments.SingleAsync(e => e.Name == "Production");
            env.Status = "SoftDeleted";
            env.SoftDeletedOn = DateTime.UtcNow.AddDays(-2);
            env.HardDeletePendingOn = DateTime.UtcNow.AddDays(12);
            await ctx.SaveChangesAsync();
        }

        var cut = _ctx.Render<UpgradesPage>();

        cut.WaitForAssertion(() => cut.FindAll(".empty-state").Should().NotBeEmpty());
        cut.FindAll(".data-table tbody tr").Should().BeEmpty();
        cut.Markup.Should().NotContain("Production");
    }

    [Fact]
    public async Task The_first_column_names_the_record_the_way_the_rest_of_the_app_does()
    {
        await SeedOneEnvironmentAsync();

        var cut = RenderWithOneRow();

        var headers = cut.FindAll(".data-table thead th").Select(h => h.TextContent.Trim()).ToList();
        // "Customer" here and "Solution" one click away on Environments named the same
        // record twice (#807).
        headers.Should().Contain("Solution");
        headers.Should().NotContain("Customer");
    }

    [Fact]
    public async Task The_environment_leads_the_row_and_the_first_link_opens_it()
    {
        await SeedOneEnvironmentAsync();

        var cut = RenderWithOneRow();

        var headers = cut.FindAll(".data-table thead th").Select(h => h.TextContent.Trim()).Where(h => h.Length > 0).ToList();
        headers.IndexOf("Environment").Should().BeLessThan(headers.IndexOf("Solution"));
        cut.Find(".data-table tbody tr a").GetAttribute("href").Should().StartWith("/environments/",
            "a row in a list of environments opens the environment first");
    }

    [Fact]
    public async Task A_bare_visit_opens_on_the_view_this_browser_picked_last()
    {
        await SeedOneEnvironmentAsync();
        _ctx.JSInterop.Setup<string?>("sessionStorage.getItem", "aldt-upgrades-view").SetResult("0Production");
        var nav = _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

        var cut = _ctx.Render<UpgradesPage>();

        cut.WaitForAssertion(() => nav.Uri.Should().EndWith("/upgrades?type=Production"));
    }

    [Fact]
    public async Task An_address_that_names_a_filter_outranks_the_remembered_view()
    {
        await SeedOneEnvironmentAsync();
        _ctx.JSInterop.Setup<string?>("sessionStorage.getItem", "aldt-upgrades-view").SetResult("0Production");
        var nav = _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("waiting", "1"));
        var before = nav.Uri;

        var cut = _ctx.Render<UpgradesPage>();
        cut.WaitForAssertion(() => cut.FindAll(".data-table").Should().NotBeEmpty());

        nav.Uri.Should().Be(before, "a link somebody sent shows what they meant it to show");
    }

    [Fact]
    public async Task History_opens_from_the_row_menu_and_no_data_cell_holds_a_button()
    {
        await SeedOneEnvironmentAsync();

        var cut = RenderWithOneRow();

        var headers = cut.FindAll(".data-table thead th").Select(h => h.TextContent.Trim()).ToList();
        headers.Last().Should().Be("Actions");

        // An action in a data cell made every row three lines tall (#805). The sheet
        // puts the row's commands in one kebab in the trailing cell, history first
        // because it is the entry every row has and the only one that changes nothing.
        var cells = cut.FindAll(".data-table tbody tr td");
        var items = cells.Last().QuerySelectorAll(".ra__menu .menu__item")
            .Select(i => i.TextContent.Trim()).ToList();
        items.Should().Equal(
            "Update history", "Move this date to the latest", "Start this update...",
            "Change the next version...", "Open environment", "Open in Business Central");
        // The tenant comes from the solution's own connection; the name is a path segment.
        cells.Last().QuerySelector("a.menu__item[target=_blank]")!.GetAttribute("href").Should().Be(
            "https://businesscentral.dynamics.com/11111111-2222-3333-4444-555555555555/Production");
        cells.Take(cells.Count - 1).SelectMany(c => c.QuerySelectorAll("button")).Should().BeEmpty();

        cells.Last().QuerySelector(".menu__item")!.Click();
        cut.WaitForAssertion(() => cut.Find("tr.is-subrow .upg-feed__title").TextContent
            .Should().Be("Update history - CRONUS Denmark, Production"));
    }

    [Fact]
    public async Task Commands_that_need_a_selection_are_disabled_until_a_row_is_ticked()
    {
        await SeedOneEnvironmentAsync();

        var cut = RenderWithOneRow();

        // One bar, not a filter row over a selection row: search, view, commands - the
        // order Solutions and Environments use (#966).
        cut.Find(".cmdbar .cmdbar__row > .search.cmdbar__search:first-child input[type=search]").Should().NotBeNull();
        cut.Find(".cmdbar .cmdbar__row > .search + .cmdbar__group select").Should().NotBeNull();
        cut.FindAll(".filter-bar").Should().BeEmpty();

        var commands = cut.FindAll(".cmdbar .cmdbar__group:last-child button");
        commands.Select(c => c.TextContent.Trim()).Should().Equal(
            "Move dates", "Start update...", "Change the next version...", "Refresh");
        commands[0].HasAttribute("disabled").Should().BeTrue();
        commands[1].HasAttribute("disabled").Should().BeTrue();
        commands[2].HasAttribute("disabled").Should().BeTrue();
        commands[3].HasAttribute("disabled").Should().BeFalse();
        // The page's one primary.
        cut.FindAll(".btn--primary").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Move dates");

        cut.Find("tbody .data-table__col-check input").Change(true);

        cut.WaitForAssertion(() =>
        {
            cut.Find("tbody tr").ClassList.Should().Contain("is-selected");
            cut.FindAll(".cmdbar .cmdbar__group:last-child button")[0].HasAttribute("disabled").Should().BeFalse();
        });
    }

    [Fact]
    public async Task A_rows_result_gets_a_line_of_its_own_under_the_row()
    {
        await SeedOneEnvironmentAsync();
        var cut = RenderWithOneRow();

        cut.Find("tbody .data-table__col-check input").Change(true);
        cut.WaitForAssertion(() =>
            cut.FindAll(".cmdbar .cmdbar__group:last-child button")[0].HasAttribute("disabled").Should().BeFalse());
        cut.FindAll(".cmdbar .cmdbar__group:last-child button")[0].Click();
        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions .btn")
            .Should().Contain(b => b.TextContent.Trim() == "Move the dates"));
        cut.FindAll(".confirm-dialog__actions .btn").First(b => b.TextContent.Trim() == "Move the dates").Click();

        // Inside the Next update cell a long refusal from Business Central widened that
        // column and squeezed the others; on its own row it cannot size a column.
        cut.WaitForAssertion(() =>
        {
            cut.Find("tr.upg-result-row .upg-result .upg-note").TextContent.Should().Contain("Skipped");
            cut.FindAll("tbody tr:not(.is-subrow) .upg-note--muted").Should().BeEmpty();
        });
    }

    [Fact]
    public async Task State_is_a_named_glyph_and_the_type_sits_under_the_environment()
    {
        await SeedOneEnvironmentAsync();

        var cut = RenderWithOneRow();

        cut.Find("tbody tr").ClassList.Should().Contain("is-published");
        cut.Find("tbody .data-table__col-state [role=img]").GetAttribute("aria-label").Should().Be("Running");
        cut.FindAll("tbody .status-pill").Should().BeEmpty();
        cut.Find("tbody .cell-stack__main").TextContent.Should().Be("Production");
        cut.Find("tbody .cell-stack__sub").TextContent.Should().Be("Production");
        cut.Find(".pager__count").TextContent.Should().Be("Showing 1 of 1 environment");
    }

    [Fact]
    public async Task Typing_in_the_search_box_filters_the_rows_already_loaded()
    {
        await SeedOneEnvironmentAsync();

        var cut = RenderWithOneRow();

        cut.Find(".cmdbar__search input").Input("nothing like it");

        // Waited for, not read straight off: the table is drawn by the list frame, and
        // under a loaded test run the redraw has been seen to land a beat later.
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".data-table tbody tr").Should().BeEmpty();
            cut.Find(".empty-state__title").TextContent.Should().Be("No environments match these filters");
        });
    }

    // ── The solution's short name (#966) ───────────────────────────────

    [Fact]
    public async Task The_short_name_shows_after_the_solution_and_the_search_finds_it()
    {
        await SeedFleetEnvironmentAsync("CRONUS Denmark", "Production", shortName: "CRD");
        await SeedFleetEnvironmentAsync("Fabrikam Norway", "Production");

        var cut = _ctx.Render<UpgradesPage>();
        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(2));

        var shortName = cut.Find("td.upg-customer .sol-list__short");
        shortName.TextContent.Should().Be("CRD");
        shortName.GetAttribute("title").Should().Be("Short name");

        // Colleagues say "CRD" aloud; a search for it in any case finds the customer.
        cut.WaitForAssertion(() =>
        {
            cut.Find(".cmdbar__search input").Input("crd");
            var rows = cut.FindAll(".data-table tbody tr");
            rows.Should().ContainSingle();
            rows[0].QuerySelector("td.upg-customer a")!.TextContent.Should().Be("CRONUS Denmark");
        });
    }

    // ── Change the next version (#960) ──────────────────────────────────

    /// <summary>
    /// One environment per preview group the page can reach from a selection of rows it
    /// may act on: 27.5 moving forward, 30.0 moving back, one already on a later version,
    /// one already set to 29.2, one not offered it, one with an update running.
    /// </summary>
    private async Task SeedVersionFleetAsync()
    {
        await SeedFleetEnvironmentAsync("CRONUS Denmark", "Production",
            nextVersion: "27.5", offered: ["29.2", "27.5"]);
        await SeedFleetEnvironmentAsync("CRONUS Denmark", "Sandbox", type: "Sandbox",
            nextVersion: "30.0", offered: ["30.0", "29.2"]);
        await SeedFleetEnvironmentAsync("Fabrikam Norway", "Production",
            version: "29.3.1.0", nextVersion: "30.0", offered: ["30.0"]);
        await SeedFleetEnvironmentAsync("Litware Sweden", "Production",
            nextVersion: "29.2", offered: ["29.2"]);
        await SeedFleetEnvironmentAsync("Northwind Finland", "Production",
            nextVersion: "28.1", offered: ["28.1"]);
        await SeedFleetEnvironmentAsync("Tailspin Iceland", "Production",
            nextVersion: "29.2", offered: ["29.2"], nextStatus: "Running");
    }

    private IRenderedComponent<UpgradesPage> OpenVersionDialog(int rows)
    {
        var cut = _ctx.Render<UpgradesPage>();
        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(rows));

        cut.WaitForAssertion(() =>
        {
            cut.Find("thead .data-table__col-check input").Change(true);
            cut.FindAll(".cmdbar .cmdbar__group:last-child button")[2].HasAttribute("disabled").Should().BeFalse();
        });
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".cmdbar .cmdbar__group:last-child button")[2].Click();
            cut.Find(".confirm-dialog__title").TextContent.Should().Be("Change the next version?");
        });
        return cut;
    }

    private static void Pick(IRenderedComponent<UpgradesPage> cut, string version) =>
        cut.FindAll(".upg-version__opts input[type=radio]")
            .First(r => r.GetAttribute("value") == version)
            .Change(version);

    [Fact]
    public async Task The_picker_lists_every_version_on_offer_newest_first_with_how_many_it_is_for()
    {
        await SeedVersionFleetAsync();

        var cut = OpenVersionDialog(rows: 6);

        cut.WaitForAssertion(() =>
        {
            var options = cut.FindAll(".upg-version__opts .upg-when__opt")
                .Select(o => o.QuerySelector(".upg-version__name")!.TextContent + " "
                             + o.QuerySelector(".upg-version__count")!.TextContent)
                .ToList();
            options.Should().Equal(
                "30.0 for 2 of the selected",
                "29.2 for 4 of the selected",
                "28.1 for 1 of the selected",
                "27.5 for 1 of the selected");
            // Nothing is picked for the person, so there is nothing to preview or confirm yet.
            cut.FindAll(".upg-preview").Should().BeEmpty();
            cut.FindAll(".confirm-dialog__actions .btn").Last().HasAttribute("disabled").Should().BeTrue();
        });
    }

    [Fact]
    public async Task Picking_a_version_sorts_every_selected_environment_into_its_group()
    {
        await SeedVersionFleetAsync();
        var cut = OpenVersionDialog(rows: 6);

        cut.WaitForAssertion(() =>
        {
            Pick(cut, "29.2");
            var heads = cut.FindAll(".upg-preview__head").Select(h => h.TextContent.Trim()).ToList();
            heads.Should().Equal(
                "Will change (2)", "Already on it (1)", "Already chosen (1)", "Not offered (1)", "Update under way (1)");
        });

        cut.WaitForAssertion(() =>
        {
            var lines = cut.FindAll(".upg-preview li")
                .Select(li => li.QuerySelector(".upg-preview__what")!.TextContent.Trim())
                .ToList();
            lines.Should().Contain("27.5 to 29.2");
            lines.Should().Contain("30.0 back to 29.2");
            lines.Should().Contain("Already on 29.3, past 29.2");
            lines.Should().Contain("Already set to 29.2");
            lines.Should().Contain("Business Central does not offer 29.2 to this environment yet");
            lines.Should().Contain("An update is already running, and Microsoft finishes it");

            cut.Find(".upg-version__date").TextContent.Trim()
                .Should().Be("Business Central sets the date; use Move dates afterwards to push it out.");
            cut.Find(".confirm-dialog .upg-preview__count").TextContent
                .Should().Be("2 environments, including 1 production.");
        });
    }

    [Fact]
    public async Task The_change_waits_for_the_typed_word_like_the_other_production_writes()
    {
        await SeedVersionFleetAsync();
        var cut = OpenVersionDialog(rows: 6);

        cut.WaitForAssertion(() =>
        {
            Pick(cut, "29.2");
            cut.Find(".confirm-dialog__gate-label").TextContent.Should().Contain("update");
            var confirm = cut.FindAll(".confirm-dialog__actions .btn").Last();
            confirm.TextContent.Trim().Should().Be("Change to 29.2");
            confirm.HasAttribute("disabled").Should().BeTrue();
        });

        cut.WaitForAssertion(() =>
        {
            cut.Find(".confirm-dialog__gate input").Input("update");
            cut.FindAll(".confirm-dialog__actions .btn").Last().HasAttribute("disabled").Should().BeFalse();
        });
    }

    [Fact]
    public async Task A_version_nobody_in_the_selection_can_move_to_holds_the_confirm()
    {
        await SeedVersionFleetAsync();
        var cut = OpenVersionDialog(rows: 6);

        // 28.1 is offered only to the environment already set to it.
        cut.WaitForAssertion(() =>
        {
            Pick(cut, "28.1");
            cut.FindAll(".upg-preview__head").Select(h => h.TextContent.Trim())
                .Should().NotContain(h => h.StartsWith("Will change"));
            cut.Find(".confirm-dialog .upg-preview__count").TextContent.Should().Be("Nothing to change for this version.");
        });
        cut.WaitForAssertion(() =>
        {
            cut.Find(".confirm-dialog__gate input").Input("update");
            cut.FindAll(".confirm-dialog__actions .btn").Last().HasAttribute("disabled").Should().BeTrue();
        });
    }

    private async Task SeedFleetEnvironmentAsync(
        string projectName, string environmentName, string type = "Production", string? shortName = null,
        string version = "27.4.12345.0", string? nextVersion = null, List<string>? offered = null,
        string? nextStatus = "Scheduled")
    {
        await using var ctx = _db.NewContext();
        var project = await ctx.OeProjects.FirstOrDefaultAsync(p => p.Name == projectName);
        if (project is null)
        {
            project = new OeProject
            {
                OrganizationId = TestDb.DefaultOrgId,
                Name = projectName,
                ShortName = shortName,
                BcTenantId = TenantId,
                CreatedByUserId = AdminUserId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            ctx.OeProjects.Add(project);
            await ctx.SaveChangesAsync();
        }

        ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = project.Id,
            Name = environmentName,
            Type = type,
            ApplicationFamily = "BusinessCentral",
            Status = "Active",
            Version = version,
            FetchedAt = DateTime.UtcNow,
            BcNextUpdateVersion = nextVersion,
            BcNextUpdateStatus = nextVersion is null ? null : nextStatus,
            BcOfferedVersions = offered,
            BcNextUpdateFetchedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public void A_row_without_a_tenant_has_no_Business_Central_address_and_a_name_is_escaped()
    {
        var row = new UpgradeFleetRow(1, "CRONUS Denmark", null, 2, "UAT 2", "Sandbox", "Active", null,
            null, null, null, null, null, null, null, CanAct: true);

        row.BusinessCentralUrl.Should().BeNull();
        (row with { TenantId = TenantId }).BusinessCentralUrl.Should().EndWith("/UAT%202");
    }
}
