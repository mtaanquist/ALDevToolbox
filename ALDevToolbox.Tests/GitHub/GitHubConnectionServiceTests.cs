using System.Security.Cryptography;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The per-organisation half of the GitHub App connection (issue #620): what
/// the install callback writes, what the Repositories tab reads back, and the
/// rules that keep a connection which cannot work from being stored in the
/// first place.
/// </summary>
public sealed class GitHubConnectionServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private GitHubConnectionService NewService() => _db.NewGitHubConnectionService(_db.NewContext());

    private static GitHubInstallation Installation(
        long id = 42,
        string login = "cronus-dk",
        string accountType = "Organization",
        params (string Name, string Level)[] permissions) =>
        new(id, login, accountType,
            permissions.Length == 0
                ? new Dictionary<string, string> { ["metadata"] = "read", ["contents"] = "write", ["administration"] = "write" }
                : permissions.ToDictionary(p => p.Name, p => p.Level));

    private async Task ConfigureDeploymentAsync()
    {
        using var rsa = RSA.Create(2048);
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-dev-toolbox", ClientId: null,
            ClientSecret: null, ClearClientSecret: false,
            PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));
    }

    // --- Status -------------------------------------------------------------

    [Fact]
    public async Task Status_starts_out_not_connected_and_not_configured()
    {
        var status = await NewService().GetStatusAsync();

        status.DeploymentConfigured.Should().BeFalse();
        status.IsConnected.Should().BeFalse();
        status.OrgLogin.Should().BeNull();
        status.Permissions.Should().BeEmpty();
        status.CanCreateRepositories.Should().BeFalse();
    }

    [Fact]
    public async Task Status_reports_the_deployment_registration_separately_from_the_connection()
    {
        await ConfigureDeploymentAsync();

        var status = await NewService().GetStatusAsync();

        status.DeploymentConfigured.Should().BeTrue();
        status.AppSlug.Should().Be("al-dev-toolbox");
        status.IsConnected.Should().BeFalse("the deployment having an app is not the org having connected one");
    }

    // --- Connect ------------------------------------------------------------

    [Fact]
    public async Task Connect_stores_the_login_permissions_and_timestamp()
    {
        await NewService().ConnectAsync(Installation());

        var status = await NewService().GetStatusAsync();
        status.IsConnected.Should().BeTrue();
        status.InstallationId.Should().Be(42);
        status.OrgLogin.Should().Be("cronus-dk");
        status.ConnectedAt.Should().NotBeNull();
        status.Permissions.Should().ContainKey("contents").WhoseValue.Should().Be("write");
        status.CanCreateRepositories.Should().BeTrue();
    }

    [Fact]
    public async Task Connect_without_repository_creation_rights_is_stored_but_flagged()
    {
        await NewService().ConnectAsync(Installation(permissions: [("metadata", "read"), ("contents", "write")]));

        var status = await NewService().GetStatusAsync();
        status.IsConnected.Should().BeTrue("a read-only installation is still useful for the other features");
        status.CanCreateRepositories.Should().BeFalse("the tab says so before someone hits it from New Workspace");
    }

    [Fact]
    public async Task Connect_treats_a_read_level_administration_grant_as_not_enough()
    {
        await NewService().ConnectAsync(Installation(permissions: [("administration", "read")]));

        (await NewService().GetStatusAsync()).CanCreateRepositories.Should().BeFalse();
    }

    [Fact]
    public async Task Connect_refuses_a_personal_github_account()
    {
        Func<Task> act = () => NewService().ConnectAsync(Installation(login: "some-person", accountType: "User"));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("GitHubOrgLogin");
    }

    [Fact]
    public async Task Connect_refuses_an_installation_without_an_account_login()
    {
        Func<Task> act = () => NewService().ConnectAsync(Installation(login: "   "));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("GitHubOrgLogin");
    }

    [Fact]
    public async Task Connect_refuses_an_installation_id_that_is_not_positive()
    {
        Func<Task> act = () => NewService().ConnectAsync(Installation(id: 0));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("GitHubInstallationId");
    }

    [Fact]
    public async Task Connect_twice_replaces_the_previous_connection()
    {
        await NewService().ConnectAsync(Installation(id: 42, login: "cronus-dk"));
        await NewService().ConnectAsync(Installation(id: 77, login: "cronus-uk"));

        var status = await NewService().GetStatusAsync();
        status.InstallationId.Should().Be(77);
        status.OrgLogin.Should().Be("cronus-uk");

        await using var read = _db.NewContext();
        (await read.OrganizationSettings.AsNoTracking().CountAsync(s => s.OrganizationId == TestDb.DefaultOrgId))
            .Should().Be(1, "the connection lives on the org's single settings row");
    }

    // --- The cross-organisation claim guard ---------------------------------

    /// <summary>
    /// Seeds another toolbox organisation that already holds
    /// <paramref name="installationId"/>. Writes through the tracked context
    /// directly: the query filter scopes reads, not inserts, so this needs no
    /// bypass of its own.
    /// </summary>
    private async Task SeedOtherOrgConnectionAsync(long installationId)
    {
        await using var ctx = _db.NewContext();
        ctx.OrganizationSettings.Add(new ALDevToolbox.Domain.Entities.OrganizationSettings
        {
            OrganizationId = TestDb.OtherOrgId,
            GitHubInstallationId = installationId,
            GitHubOrgLogin = "cronus-uk",
            GitHubConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Connect_refuses_an_installation_another_organisation_already_holds()
    {
        // The setup callback trusts the installation_id GitHub hands back, and
        // the App JWT can read every installation of the app - so this guard is
        // what stops one tenant claiming another tenant's GitHub organisation.
        // See "Binding the installation to the acting user" in the design doc.
        await SeedOtherOrgConnectionAsync(42);

        Func<Task> act = () => NewService().ConnectAsync(Installation(id: 42));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("GitHubOrgLogin");
        (await NewService().GetStatusAsync()).IsConnected.Should().BeFalse("nothing was written");
    }

    [Fact]
    public async Task Connect_allows_the_same_organisation_to_reconnect_its_own_installation()
    {
        await NewService().ConnectAsync(Installation(id: 42));

        Func<Task> act = () => NewService().ConnectAsync(Installation(id: 42, permissions: [("contents", "write")]));

        await act.Should().NotThrowAsync("the guard excludes the acting org, so re-connecting must still work");
        var status = await NewService().GetStatusAsync();
        status.InstallationId.Should().Be(42);
        status.Permissions.Should().ContainSingle();
    }

    [Fact]
    public async Task Connect_allows_a_different_installation_while_another_org_holds_one()
    {
        await SeedOtherOrgConnectionAsync(42);

        await NewService().ConnectAsync(Installation(id: 43));

        (await NewService().GetStatusAsync()).InstallationId.Should().Be(43);
    }

    // --- Refresh ------------------------------------------------------------

    [Fact]
    public async Task Refresh_updates_the_permissions_and_login_without_moving_the_connected_date()
    {
        await NewService().ConnectAsync(Installation(permissions: [("metadata", "read")]));
        var connectedAt = (await NewService().GetStatusAsync()).ConnectedAt;

        await NewService().RefreshAsync(Installation(
            login: "cronus-dk-renamed", permissions: [("metadata", "read"), ("administration", "write")]));

        var status = await NewService().GetStatusAsync();
        status.OrgLogin.Should().Be("cronus-dk-renamed");
        status.CanCreateRepositories.Should().BeTrue("widening the app's access on GitHub shows up here");
        status.ConnectedAt.Should().Be(connectedAt, "it is the same connection, so its date must not move");
    }

    [Fact]
    public async Task Refresh_does_nothing_when_the_organisation_is_not_connected()
    {
        await NewService().RefreshAsync(Installation());

        (await NewService().GetStatusAsync()).IsConnected.Should().BeFalse("refresh updates a connection, it never makes one");
    }

    [Fact]
    public async Task Refresh_ignores_an_installation_that_is_not_the_connected_one()
    {
        await NewService().ConnectAsync(Installation(id: 42, permissions: [("metadata", "read")]));

        await NewService().RefreshAsync(Installation(id: 99, login: "someone-else", permissions: [("administration", "write")]));

        var status = await NewService().GetStatusAsync();
        status.InstallationId.Should().Be(42);
        status.OrgLogin.Should().Be("cronus-dk");
        status.CanCreateRepositories.Should().BeFalse();
    }

    // --- Disconnect ---------------------------------------------------------

    [Fact]
    public async Task Disconnect_clears_every_github_column()
    {
        await NewService().ConnectAsync(Installation());

        await NewService().DisconnectAsync();

        var status = await NewService().GetStatusAsync();
        status.IsConnected.Should().BeFalse();
        status.OrgLogin.Should().BeNull();
        status.ConnectedAt.Should().BeNull();
        status.Permissions.Should().BeEmpty();

        await using var read = _db.NewContext();
        var row = await read.OrganizationSettings.AsNoTracking()
            .FirstAsync(s => s.OrganizationId == TestDb.DefaultOrgId);
        row.GitHubInstallationPermissions.Should().BeNull();
    }

    [Fact]
    public async Task Disconnect_on_an_unconnected_org_is_a_no_op()
    {
        Func<Task> act = () => NewService().DisconnectAsync();

        await act.Should().NotThrowAsync();
        (await NewService().GetStatusAsync()).IsConnected.Should().BeFalse();
    }

    // --- Tenant scope -------------------------------------------------------

    [Fact]
    public async Task Connect_writes_only_to_the_acting_organisation()
    {
        await NewService().ConnectAsync(Installation());

        await using var read = _db.NewContext();
        var others = await read.OrganizationSettings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.OrganizationId != TestDb.DefaultOrgId && s.GitHubInstallationId != null)
            .CountAsync();
        others.Should().Be(0);
    }
}
