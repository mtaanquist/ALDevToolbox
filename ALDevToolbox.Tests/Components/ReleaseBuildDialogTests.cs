using ALDevToolbox.Components.Shared;
using ALDevToolbox.Data;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using Bunit;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The publish confirmation is the point of no return on the most destructive
/// action in the product, so it has to say <em>whose</em> tenant is about to be
/// written to. Environments are routinely named literally "Production", which is
/// exactly the case where the environment name alone identifies nothing (#714).
///
/// The dialog is opened imperatively and never reaches the database here - the
/// <see cref="DeliveryService"/> it injects is constructed but not called, so
/// the context is pointed at a connection string that is never opened.
/// </summary>
public sealed class ReleaseBuildDialogTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public ReleaseBuildDialogTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(NewUnusedDeliveryService());
    }

    public void Dispose() => _ctx.Dispose();

    private static DeliveryService NewUnusedDeliveryService()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=never-opened")
            .Options;
        var db = new AppDbContext(options);
        var org = new SignedOutOrganizationContext();
        return new DeliveryService(db, org,
            new ProjectAccess(db, org),
            new UnusedTokenSource(), new UnusedAppManagementClient(), new UnusedAdminClient(),
            new DeliveryQueue(), NullLogger<DeliveryService>.Instance);
    }

    private IRenderedComponent<ReleaseBuildDialog> OpenedDialog(string envName, string envType = "Production")
    {
        var cut = _ctx.Render<ReleaseBuildDialog>();
        cut.InvokeAsync(() => cut.Instance.OpenAsync(
            releasePipelineId: 1,
            customerName: "CRONUS A/S",
            envName: envName,
            envType: envType,
            deploymentSchedule: "Immediate",
            schemaSyncMode: "Add",
            builds:
            [
                new ReleaseBuildDialog.ReleasableBuildOption(412, "#412 · main @a3f9c21 · 2 apps · built 29 Aug 14:21",
                [
                    new ReleaseBuildDialog.ReleasableBuildApp("CRONUS Sales Extension", "1.4.2.0"),
                    new ReleaseBuildDialog.ReleasableBuildApp("CRONUS Shared Library", "3.0.1.0"),
                ]),
            ],
            timeZone: "Europe/Copenhagen",
            windowStart: null,
            windowEnd: null)).GetAwaiter().GetResult();
        return cut;
    }

    [Fact]
    public void An_environment_literally_named_Production_still_names_the_customer_in_the_title_and_the_acknowledgement()
    {
        var cut = OpenedDialog("Production");

        cut.Find("#rb-title").TextContent.Should().Be("Release to CRONUS A/S — Production");

        var ack = cut.Find(".check--ack").TextContent;
        ack.Should().Contain("CRONUS A/S");
        ack.Should().Contain("Production");

        cut.Find(".confirm-dialog__body p").TextContent
            .Should().Contain("to Production in CRONUS A/S's Business Central");
    }

    [Fact]
    public void The_dialog_lists_the_selected_builds_apps_and_versions()
    {
        var cut = OpenedDialog("Production");

        var apps = cut.FindAll(".rb-apps__list li").Select(li => li.TextContent.Trim()).ToList();
        apps.Should().HaveCount(2);
        apps[0].Should().Contain("CRONUS Sales Extension").And.Contain("1.4.2.0");
        apps[1].Should().Contain("CRONUS Shared Library").And.Contain("3.0.1.0");
    }

    [Fact]
    public void A_non_production_target_names_the_customer_and_asks_for_no_acknowledgement()
    {
        var cut = OpenedDialog("UAT", envType: "Sandbox");

        cut.Find("#rb-title").TextContent.Should().Be("Release to CRONUS A/S — UAT");
        cut.FindAll(".check--ack").Should().BeEmpty();
    }

    // ── Unused seams: the dialog never releases in these tests ────────────────

    /// <summary>No signed-in user: nothing here queries, so the filter sentinel is enough.</summary>
    private sealed class SignedOutOrganizationContext : IOrganizationContext
    {
        public int? CurrentOrganizationId => null;
        public int? CurrentUserId => null;
        public bool IsSiteAdmin => false;
        public bool IsSystemOrganization => false;
        public int OrganizationIdForFilter => 0;
    }

    private sealed class UnusedTokenSource : IDeliveryTokenSource
    {
        public Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class UnusedAdminClient : IBcAdminClient
    {
        public Task<IReadOnlyList<BcEnvironment>> ListEnvironmentsAsync(string accessToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcEnvironment?> GetEnvironmentAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcUpdateSettings?> GetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BcEnvironmentUpdate>> ListEnvironmentUpdatesAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BcTimeZone>> ListTimezonesAsync(string accessToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetAppUpdateCadenceAsync(string accessToken, string? applicationFamily, string environmentName, string cadence, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool?> GetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SelectTargetVersionAsync(string accessToken, string? applicationFamily, string environmentName, string targetVersion, string? targetVersionType, DateTimeOffset? selectedDateTime = null, bool? ignoreUpdateWindow = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, TimeOnly start, TimeOnly end, string windowsTimeZoneId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class UnusedAppManagementClient : IBcAppManagementClient
    {
        public Task<BcAppOperation> InstallPteAsync(string accessToken, string applicationFamily, string environmentName, byte[] appBytes, string fileName, string deploymentSchedule, string syncMode, string languageId, bool installOrUpdateNeededDependencies, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcAppOperation?> GetAppOperationAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, Guid operationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BcInstalledApp>> ListInstalledAppsAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BcScheduledPteOperation>> ListScheduledPteOperationsAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BcAvailableAppUpdate>> ListAvailableUpdatesAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcAppOperation> RemoveScheduledPteVersionAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, string targetVersion, string scheduleKind, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
