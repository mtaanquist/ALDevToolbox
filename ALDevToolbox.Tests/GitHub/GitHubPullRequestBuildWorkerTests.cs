using System.Security.Cryptography;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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

    // --- Fixture -----------------------------------------------------------

    /// <summary>
    /// A worker over a service provider that hands out a fresh scope per
    /// organisation, exactly as the hosted worker's own does - the point of the
    /// test is that each read happens under its own tenant filter.
    /// </summary>
    private GitHubPullRequestBuildWorker NewWorker()
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
        _db.AddGitHubServices(services, new FakeGitHubApi());

        var provider = services.BuildServiceProvider();
        return new GitHubPullRequestBuildWorker(
            new GitHubWebhookQueue(), provider,
            NullLogger<GitHubPullRequestBuildWorker>.Instance,
            new ALDevToolbox.Services.WorkerHeartbeatRegistry(TimeProvider.System));
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
