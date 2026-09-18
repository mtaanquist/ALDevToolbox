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
/// <para>Both rules pinned here are layout ones that only a render can show, and both
/// were shipped wrong once (#805, #807): the row's way into its history belongs in a
/// column of its own rather than inside the Environment cell, and the search box and
/// the button that submits it have to be one row - a `form` is a block, so the button
/// drops underneath the box unless something makes the form lay out across.</para>
/// </summary>
public sealed class UpgradesPageTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private const int AdminUserId = 9850;

    public UpgradesPageTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("upgrades@example.com");

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
    public async Task The_history_toggle_is_a_column_of_its_own_not_part_of_the_environment_cell()
    {
        await SeedOneEnvironmentAsync();

        var cut = RenderWithOneRow();

        var headers = cut.FindAll(".data-table thead th").Select(h => h.TextContent.Trim()).ToList();
        headers.Last().Should().Be("History");

        // An action in a data cell made every row three lines tall (#805): the toggle
        // lives in the trailing cell now, and the Environment cell is back to a name
        // and its type.
        var cells = cut.FindAll(".data-table tbody tr td");
        var history = cells.Last();
        var toggle = history.QuerySelector("button");
        toggle.Should().NotBeNull();
        // A verb, like every other button here, and it says which way it goes.
        toggle!.TextContent.Trim().Should().Be("Show history");
        // The button still announces whether the panel under the row is open.
        toggle.GetAttribute("aria-expanded").Should().Be("false");

        // The toggle is all the cell holds: a booking reads beside the date it
        // competes with, in the next-update cell.
        history.QuerySelectorAll("button").Should().HaveCount(1);
        cut.Find(".upg-env").TextContent.Should().NotContain("history");
    }

    [Fact]
    public async Task The_search_box_and_its_button_are_in_one_form_that_lays_them_out_across()
    {
        await SeedOneEnvironmentAsync();

        var cut = RenderWithOneRow();

        // The sizing class belongs on the box; the form is the row holding the box and
        // the button that submits it. With the class on the form itself, the button
        // dropped underneath the box and the filters beside it read as a jumble (#805).
        var form = cut.Find(".filter-bar form.upg-search");
        form.QuerySelector("span.search.filter-bar__search input[type=search]").Should().NotBeNull();
        form.QuerySelector("button[type=submit]")!.TextContent.Trim().Should().Be("Search");
        form.ClassList.Should().NotContain("filter-bar__search");
    }

    // ── Business Central is never reached by a render ───────────────────

    private sealed class UnreachableHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }

    private sealed class UnreachableAdminClient : IBcAdminClient
    {
        public Task<IReadOnlyList<BcEnvironment>> ListEnvironmentsAsync(string accessToken, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<BcEnvironment?> GetEnvironmentAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<BcEnvironmentUpdate>> ListEnvironmentUpdatesAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SelectTargetVersionAsync(string accessToken, string? applicationFamily, string environmentName, string targetVersion, string? targetVersionType, DateTimeOffset? selectedDateTime = null, bool? ignoreUpdateWindow = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<BcUpdateSettings?> GetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, TimeOnly start, TimeOnly end, string windowsTimeZoneId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<BcTimeZone>> ListTimezonesAsync(string accessToken, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SetAppUpdateCadenceAsync(string accessToken, string? applicationFamily, string environmentName, string cadence, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool?> GetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, bool enabled, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class UnreachableAppManagementClient : IBcAppManagementClient
    {
        public Task<IReadOnlyList<BcInstalledApp>> ListInstalledAppsAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<BcAvailableAppUpdate>> ListAvailableUpdatesAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<BcScheduledPteOperation>> ListScheduledPteOperationsAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<BcAppOperation> RemoveScheduledPteVersionAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, string targetVersion, string scheduleKind, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<BcAppOperation> InstallPteAsync(string accessToken, string applicationFamily, string environmentName, byte[] appBytes, string fileName, string deploymentSchedule, string syncMode, string languageId, bool installOrUpdateNeededDependencies, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<BcAppOperation?> GetAppOperationAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, Guid operationId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
