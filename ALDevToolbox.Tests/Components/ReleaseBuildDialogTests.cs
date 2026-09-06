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
using ALDevToolbox.Services.Operations;

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
        _ctx.Services.AddSingleton(NewUnusedGitHubReleaseService());
    }

    public void Dispose() => _ctx.Dispose();

    /// <summary>
    /// The dialog injects the Releases service so a Release-sourced pipeline can stage
    /// its apps when the person releases. Nothing here releases, so this one is
    /// constructed and never called - like the delivery service beside it.
    /// </summary>
    private static ALDevToolbox.Services.GitHub.GitHubReleaseService NewUnusedGitHubReleaseService()
    {
        var db = NewUnopenedContext();
        var org = new SignedOutOrganizationContext();
        var settings = new SystemSettingsService(db, NewProtection(), NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
        var client = new ALDevToolbox.Services.GitHub.GitHubAppClient(
            new HttpClient(new UnreachableHandler()) { BaseAddress = new Uri(ALDevToolbox.Services.GitHub.GitHubAppClient.ApiBaseUrl) },
            settings, new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            TimeProvider.System, NullLogger<ALDevToolbox.Services.GitHub.GitHubAppClient>.Instance);
        return new ALDevToolbox.Services.GitHub.GitHubReleaseService(
            db, client,
            // Constructed so the service resolves; nothing here asks it anything.
            new ALDevToolbox.Services.GitHub.GitHubConnectionService(
                db, org, null!, settings, null!,
                NullLogger<ALDevToolbox.Services.GitHub.GitHubConnectionService>.Instance, TimeProvider.System),
            new ProjectAccess(db, org), org,
            new ALDevToolbox.Endpoints.PublicOrigin(null), TimeProvider.System,
            NullLogger<ALDevToolbox.Services.GitHub.GitHubReleaseService>.Instance);
    }

    private static Microsoft.AspNetCore.DataProtection.IDataProtectionProvider NewProtection() =>
        Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aldt-release-dialog-tests")));

    private static AppDbContext NewUnopenedContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=never-opened").Options);

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("GitHub is not reachable from this test.");
    }

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
            new DeliveryQueue(),
            new ALDevToolbox.Services.ObjectExplorer.Bc.BcPanelCache(TimeProvider.System),
            NullLogger<DeliveryService>.Instance);
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

    [Fact]
    public void A_release_sourced_pipeline_picks_a_github_release_and_shows_the_files_it_installs()
    {
        var cut = _ctx.Render<ReleaseBuildDialog>();
        cut.InvokeAsync(() => cut.Instance.OpenAsync(
            releasePipelineId: 1,
            customerName: "CRONUS A/S",
            envName: "Production",
            envType: "Production",
            deploymentSchedule: "Immediate",
            schemaSyncMode: "Add",
            builds: [],
            timeZone: "Europe/Copenhagen",
            windowStart: null,
            windowEnd: null,
            secretExpiresAt: null,
            releases:
            [
                new ALDevToolbox.Services.GitHub.GitHubReleaseOption(
                    "v1.4.2.0", "v1.4.2.0", new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero),
                    ["CRONUS Sales Extension_1.4.2.0.app"]),
                // Nothing installable on it, so it is not offered at all.
                new ALDevToolbox.Services.GitHub.GitHubReleaseOption("v1.4.1.0", null, null, []),
            ],
            repositoryName: "cronus-customer")).GetAwaiter().GetResult();

        // The build picker is gone: this pipeline has no builds of its own.
        cut.FindAll("#rb-build").Should().BeEmpty();
        var tags = cut.FindAll("#rb-release option").Select(o => o.TextContent.Trim()).ToList();
        tags.Should().ContainSingle().Which.Should().StartWith("v1.4.2.0");
        cut.Markup.Should().Contain("This release installs");
        cut.Markup.Should().Contain("CRONUS Sales Extension_1.4.2.0.app");
        cut.Markup.Should().Contain("cronus-customer");
    }

    [Fact]
    public async Task A_repository_with_no_installable_release_says_what_to_publish_instead_of_an_empty_picker()
    {
        var cut = _ctx.Render<ReleaseBuildDialog>();
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(
            releasePipelineId: 1,
            customerName: "CRONUS A/S",
            envName: "Production",
            envType: "Sandbox",
            deploymentSchedule: "Immediate",
            schemaSyncMode: "Add",
            builds: [],
            timeZone: "Europe/Copenhagen",
            windowStart: null,
            windowEnd: null,
            secretExpiresAt: null,
            // One release, nothing installable attached to it.
            releases: [new ALDevToolbox.Services.GitHub.GitHubReleaseOption("v1.4.1.0", null, null, [])],
            repositoryName: "cronus-customer"));

        cut.FindAll("#rb-release").Should().BeEmpty();
        cut.Markup.Should().Contain("No release on cronus-customer has an .app file attached yet.");
        cut.Markup.Should().Contain("Publish a release with the compiled apps attached");
        cut.Find(".confirm-dialog__actions .btn--primary").HasAttribute("disabled").Should().BeTrue();
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
