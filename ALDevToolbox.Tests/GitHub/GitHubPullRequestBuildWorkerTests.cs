using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The routing half of the compile gate (issue #627): turning an installation id
/// GitHub sent us back into the organisation that connected it, without a single
/// cross-tenant read.
///
/// <para>This is the piece the fence question in <c>CLAUDE.md</c> is about. One
/// <c>IgnoreQueryFilters()</c> would answer it in a query, and it would sit in
/// code an anonymous inbound request reaches. Instead the worker walks
/// <c>organizations</c> - which carries no tenant filter - and asks each
/// organisation, under its own filter and its own ambient scope, what it
/// connected. These tests pin that the walk finds the right organisation, and
/// only that one.</para>
/// </summary>
public sealed class GitHubPullRequestBuildWorkerTests : IDisposable
{
    private const long ConnectedInstallation = 42;

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task The_organisation_that_connected_the_installation_is_the_one_found()
    {
        await ConfigureDeploymentAsync();
        var otherOrgId = await SeedOrganizationAsync("Other");
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        await ConnectAsync(otherOrgId, 99, "someone-else");

        var worker = NewWorker();

        var resolved = await worker.ResolveOrganizationAsync(ConnectedInstallation, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.Value.Identity.OrganizationId.Should().Be(TestDb.DefaultOrgId);
        resolved.Value.OrgLogin.Should().Be("cronus-dk");
    }

    [Fact]
    public async Task An_installation_nobody_connected_resolves_to_nothing()
    {
        // Ordinary rather than alarming: an app can be installed on a GitHub
        // organisation that no toolbox organisation has connected, and a
        // disconnected one keeps its webhook until the installation is removed.
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");

        var resolved = await NewWorker().ResolveOrganizationAsync(4242, CancellationToken.None);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task With_no_organisation_connected_at_all_nothing_resolves()
    {
        await ConfigureDeploymentAsync();

        var resolved = await NewWorker().ResolveOrganizationAsync(ConnectedInstallation, CancellationToken.None);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task The_resolved_identity_carries_the_organisations_own_system_flag()
    {
        // The flag decides how storage-quota and template-import rules treat the
        // organisation, so scheduled work has to see the same value an interactive
        // request would.
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");

        await using var ctx = _db.NewContext();
        var isSystem = await ctx.Organizations.AsNoTracking()
            .Where(o => o.Id == TestDb.DefaultOrgId).Select(o => o.IsSystem).SingleAsync();

        var resolved = await NewWorker().ResolveOrganizationAsync(ConnectedInstallation, CancellationToken.None);

        resolved!.Value.Identity.IsSystemOrganization.Should().Be(isSystem);
        resolved.Value.Identity.IsSiteAdmin.Should().BeFalse("background work acts for an organisation, never as a site admin");
        resolved.Value.Identity.UserId.Should().BeNull("a webhook build has no user behind it");
    }

    [Fact]
    public async Task A_pending_organisation_is_not_asked()
    {
        // Pending organisations are signup rows nobody has approved; the sweeps
        // skip them and so does this.
        await ConfigureDeploymentAsync();
        var pendingId = await SeedOrganizationAsync("Pending", isPending: true);
        await ConnectAsync(pendingId, ConnectedInstallation, "cronus-dk");

        var resolved = await NewWorker().ResolveOrganizationAsync(ConnectedInstallation, CancellationToken.None);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task A_delivery_that_arrives_during_a_restore_is_put_back_rather_than_built()
    {
        // The webhook route stays open through maintenance on purpose - GitHub
        // disables a hook whose deliveries keep failing - so the worker is what
        // has to keep the build off a database being rewritten under it.
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");

        var queue = new GitHubWebhookQueue();
        var maintenance = new MaintenanceModeState();
        maintenance.Enter("Restoring a backup");
        var worker = NewWorker(queue, maintenance);
        var job = NewJob();

        await worker.RunOneAsync(job, CancellationToken.None);

        queue.Reader.TryRead(out var again).Should().BeTrue("the delivery is offered again, not dropped");
        again.Should().Be(job, "the same head is built once the restore is over");
    }

    // --- Member forks (#627) -----------------------------------------------

    [Fact]
    public async Task A_member_fork_is_built_when_GitHub_confirms_the_membership()
    {
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        await SeedSolutionTrackingTheRepositoryAsync();
        var api = ApiAnswering(HttpStatusCode.NoContent);
        var builds = new ReleaseImportQueue();

        await NewWorker(api: api, builds: builds).RunOneAsync(NewJob(isMemberFork: true), CancellationToken.None);

        api.Calls.Should().Contain(c => c.Contains("/orgs/cronus-dk/members/erik"),
            "the delivery's author_association is re-checked at build time, not trusted");
        api.Calls.Should().Contain(c => c.Contains("/check-runs"), "the pull request gets an answer");
        builds.Reader.TryRead(out var queued).Should().BeTrue("a confirmed member's fork builds like any branch");
        queued!.Source.Should().BeOfType<ReleaseImportSource.PullRequestBuild>()
            .Which.ForkAuthor.Should().Be("erik", "the check run says where the code came from");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(FakeGitHubApi.Unreachable)]
    public async Task A_fork_whose_author_GitHub_does_not_confirm_is_dropped_without_a_check_run(HttpStatusCode membership)
    {
        // 404 is "not a member", 302 is "you are not in this organisation
        // either", and no answer at all is not a yes. All three refuse, and none
        // of them opens a check run - there is nothing to leave spinning on a
        // pull request the toolbox will say nothing about.
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        await SeedSolutionTrackingTheRepositoryAsync();
        var api = ApiAnswering(membership);
        var builds = new ReleaseImportQueue();

        await NewWorker(api: api, builds: builds).RunOneAsync(NewJob(isMemberFork: true), CancellationToken.None);

        api.Calls.Should().NotContain(c => c.Contains("/check-runs"));
        builds.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_pull_request_from_the_repository_itself_never_asks_about_membership()
    {
        // The membership call costs a request on the organisation's rate limit
        // and answers a question a branch pull request does not raise.
        await ConfigureDeploymentAsync();
        await ConnectAsync(TestDb.DefaultOrgId, ConnectedInstallation, "cronus-dk");
        await SeedSolutionTrackingTheRepositoryAsync();
        var api = ApiAnswering(HttpStatusCode.NoContent);
        var builds = new ReleaseImportQueue();

        await NewWorker(api: api, builds: builds).RunOneAsync(NewJob(), CancellationToken.None);

        api.Calls.Should().NotContain(c => c.Contains("/members/"));
        builds.Reader.TryRead(out var queued).Should().BeTrue();
        queued!.Source.Should().BeOfType<ReleaseImportSource.PullRequestBuild>()
            .Which.ForkAuthor.Should().BeNull("a branch of the repository is not anybody's fork");
    }

    // --- Fixture -----------------------------------------------------------

    private static GitHubPullRequestJob NewJob(bool isMemberFork = false, string authorLogin = "erik") => new(
        InstallationId: ConnectedInstallation,
        RepositoryFullName: "cronus-dk/customer-app",
        CloneUrl: "https://github.com/cronus-dk/customer-app.git",
        PullRequestNumber: 7,
        HeadSha: "abc1234",
        HeadRef: "feature/vat",
        BaseRef: "main",
        DeliveryId: "delivery-1",
        AuthorLogin: authorLogin,
        IsMemberFork: isMemberFork);

    /// <summary>
    /// A GitHub that mints an installation token, answers the membership
    /// question with <paramref name="membership"/>, and takes a check run.
    /// <see cref="FakeGitHubApi.Unreachable"/> is GitHub not answering at all.
    /// </summary>
    private static FakeGitHubApi ApiAnswering(HttpStatusCode membership)
    {
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{ConnectedInstallation}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Post, "/repos/cronus-dk/customer-app/check-runs",
                HttpStatusCode.Created, "{\"id\":555}");
        return api.On(HttpMethod.Get, "/orgs/cronus-dk/members/erik", membership);
    }

    /// <summary>A solution tracking the repository the deliveries are about.</summary>
    private async Task SeedSolutionTrackingTheRepositoryAsync()
    {
        await using var ctx = _db.NewContext();
        var project = new ALDevToolbox.Domain.Entities.ObjectExplorer.Project
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS Customer App",
            CreatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        ctx.OeProjectRepositories.Add(new ALDevToolbox.Domain.Entities.ObjectExplorer.ProjectRepository
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = project.Id,
            Url = "https://github.com/cronus-dk/customer-app.git",
            Provider = ALDevToolbox.Domain.ValueObjects.RepositoryProvider.GitHub,
            DisplayName = "customer-app",
        });
        await ctx.SaveChangesAsync();
    }


    /// <summary>
    /// A worker over a service provider that hands out a fresh scope per
    /// organisation, exactly as the hosted worker's own does - the point of the
    /// test is that each read happens under its own tenant filter.
    /// </summary>
    private GitHubPullRequestBuildWorker NewWorker(
        GitHubWebhookQueue? queue = null,
        MaintenanceModeState? maintenance = null,
        FakeGitHubApi? api = null,
        ReleaseImportQueue? builds = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        services.AddSingleton<IMemoryCache>(new MemoryCache(Options.Create(new MemoryCacheOptions())));
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(NullLogger<>));
        services.AddDataProtection();
        _db.AddStorageServices(services);
        services.AddScoped<OrganizationConfigService>();
        services.AddScoped<SystemSettingsService>();
        _db.AddGitHubServices(services, api ?? new FakeGitHubApi());

        // Everything from the check run down: a member fork that passes the gate
        // has to reach a real OpenAsync and a real StartPullRequestBuildAsync, or
        // "it was built" is only the absence of a log line.
        services.AddScoped<GitHubCheckRunService>();
        services.AddSingleton(builds ?? new ReleaseImportQueue());
        services.AddScoped<ALDevToolbox.Services.Translation.TranslationMemoryService>();
        services.AddScoped<TranslationImportService>();
        services.AddScoped<CallSiteReferenceEmitter>();
        // Built by hand rather than by the container: the dependency-drift scan is
        // an optional constructor argument the container would insist on
        // resolving, and it plays no part in a pull-request build.
        services.AddScoped(sp => new ReleaseImportService(
            sp.GetRequiredService<AppDbContext>(),
            sp.GetRequiredService<IOrganizationContext>(),
            sp.GetRequiredService<StorageQuotaGuard>(),
            sp.GetRequiredService<TranslationImportService>(),
            sp.GetRequiredService<CallSiteReferenceEmitter>(),
            NullLogger<ReleaseImportService>.Instance));
        services.AddScoped<PersistedImportJobs>();
        services.AddScoped<ProjectAccess>();
        services.AddScoped<ProjectBuildImporter>();

        var provider = services.BuildServiceProvider();
        return new GitHubPullRequestBuildWorker(
            queue ?? new GitHubWebhookQueue(), provider,
            maintenance ?? new MaintenanceModeState(),
            NullLogger<GitHubPullRequestBuildWorker>.Instance,
            new ALDevToolbox.Services.Workers.WorkerHeartbeatRegistry(TimeProvider.System))
        {
            MaintenanceRetryDelay = TimeSpan.Zero,
        };
    }

    private async Task ConfigureDeploymentAsync()
    {
        using var rsa = RSA.Create(2048);
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-dev-toolbox", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));
    }

    private async Task<int> SeedOrganizationAsync(string name, bool isPending = false)
    {
        await using var ctx = _db.NewContext();
        var org = new Organization
        {
            Name = name + " " + Guid.NewGuid().ToString("N")[..8],
            IsPending = isPending,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Organizations.Add(org);
        await ctx.SaveChangesAsync();
        return org.Id;
    }

    /// <summary>Records a connection directly, without the guarded ConnectAsync handshake.</summary>
    private async Task ConnectAsync(int organizationId, long installationId, string orgLogin)
    {
        await using var ctx = _db.NewContext();
        ctx.OrganizationSettings.Add(new OrganizationSettings
        {
            OrganizationId = organizationId,
            GitHubInstallationId = installationId,
            GitHubOrgLogin = orgLogin,
            GitHubConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }
}
