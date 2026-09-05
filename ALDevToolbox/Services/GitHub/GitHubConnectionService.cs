using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// What the Repositories tab (and, from #622 on, every GitHub feature) needs to
/// know in one read: whether the deployment has a GitHub App at all, and which
/// GitHub organisation this organisation connected to it.
/// </summary>
public sealed record GitHubConnectionStatus(
    bool DeploymentConfigured,
    string? AppSlug,
    long? InstallationId,
    string? OrgLogin,
    IReadOnlyDictionary<string, string> Permissions,
    DateTime? ConnectedAt)
{
    /// <summary>True once an admin has completed the install handshake.</summary>
    public bool IsConnected => InstallationId is not null;

    /// <summary>
    /// True when the installation may create repositories. GitHub calls this
    /// permission <c>administration</c>; without it at <c>write</c> the
    /// create-a-repository flow in #622 cannot work, so the tab says so up front.
    /// </summary>
    public bool CanCreateRepositories =>
        Permissions.TryGetValue("administration", out var level)
        && string.Equals(level, "write", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Owns the per-organisation half of the GitHub App connection: reading the
/// current connection, writing the one the install callback came back with, and
/// dropping it again.
///
/// <para>Every read and write is pinned to the acting organisation through
/// <see cref="IOrganizationContext"/>. The one exception is the uniqueness
/// probe in <see cref="ConnectAsync"/>, which asks deployment-wide whether
/// another organisation already holds an installation — a fence category 6
/// existence check that projects a bool and never a row. Writes go through
/// <see cref="AppDbContext"/> directly and then invalidate
/// <see cref="OrganizationConfigService"/>'s cache, which is what holds the
/// settings row every reader sees.</para>
///
/// <para>The service also owns the gate that decides whether the acting user is
/// entitled to the installation coming back from GitHub, which is why it holds
/// a <see cref="GitHubAccessService"/> — see <see cref="ConnectAsync"/>.</para>
///
/// <para>See <c>.design/github-integration.md</c>.</para>
/// </summary>
public sealed class GitHubConnectionService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly OrganizationConfigService _orgConfig;
    private readonly SystemSettingsService _systemSettings;
    private readonly GitHubAccessService _access;
    private readonly ILogger<GitHubConnectionService> _logger;
    private readonly TimeProvider _clock;

    public GitHubConnectionService(
        AppDbContext db,
        IOrganizationContext orgContext,
        OrganizationConfigService orgConfig,
        SystemSettingsService systemSettings,
        GitHubAccessService access,
        ILogger<GitHubConnectionService> logger,
        TimeProvider clock)
    {
        _db = db;
        _orgContext = orgContext;
        _orgConfig = orgConfig;
        _systemSettings = systemSettings;
        _access = access;
        _logger = logger;
        _clock = clock;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; GitHub connection called outside an authenticated request.");

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No user in scope; GitHub connection called outside an authenticated request.");

    /// <summary>
    /// The acting organisation's GitHub connection, plus whether the deployment
    /// has an App to connect with at all. Reads the cached organisation config,
    /// so it costs no query on a warm page.
    /// </summary>
    public async Task<GitHubConnectionStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var app = await _systemSettings.GetGitHubAppViewAsync(ct);
        var settings = (await _orgConfig.GetCurrentAsync(ct)).Settings;
        return new GitHubConnectionStatus(
            DeploymentConfigured: app.IsConfigured,
            AppSlug: app.AppSlug,
            InstallationId: settings.GitHubInstallationId,
            OrgLogin: settings.GitHubOrgLogin,
            Permissions: ParsePermissions(settings.GitHubInstallationPermissions),
            ConnectedAt: settings.GitHubConnectedAt);
    }

    /// <summary>
    /// Records the installation the admin just completed on GitHub. Validation:
    /// the installation id must be positive, the login must be non-empty, the
    /// installation must sit on a GitHub organisation — a personal-account
    /// install cannot create repositories for a team or answer "is this person
    /// in the organisation", so accepting one would only defer the failure — and
    /// no other organisation on this server may already hold it.
    /// Throws <see cref="PlanValidationException"/> with field-keyed errors.
    ///
    /// <para><strong>The acting user has to be entitled to the installation.</strong>
    /// The App JWT is authorised for <em>every</em> installation of the App, and
    /// the handshake's <c>state</c> proves only who started it, so without a
    /// further check an admin could take their own valid state, hand-edit
    /// <c>installation_id</c> to a neighbouring integer, and connect their
    /// organisation to another customer's GitHub organisation. The one
    /// credential that can settle it is the admin's own: GitHub names the
    /// organisation the installation sits on, and then says what that person is
    /// in it - only an active owner passes. Listing the installations they can
    /// reach is not enough on its own, because that list includes every
    /// installation covering a repository they merely collaborate on. That is
    /// why connecting requires a linked GitHub account, and why an answer we
    /// could not get is refused rather than assumed.</para>
    /// </summary>
    public async Task ConnectAsync(GitHubInstallation installation, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var userId = RequireUserId();
        var errors = new Dictionary<string, string>();
        if (installation.Id <= 0)
        {
            errors["GitHubInstallationId"] = "GitHub did not say which installation this was. Start again from Connect.";
        }
        if (string.IsNullOrWhiteSpace(installation.AccountLogin))
        {
            errors["GitHubOrgLogin"] = "GitHub did not say which account the app was installed on. Start again from Connect.";
        }
        else if (!installation.IsOrganization)
        {
            errors["GitHubOrgLogin"] =
                $"'{installation.AccountLogin}' is a personal GitHub account, not an organisation. Install the app on your company's GitHub organisation instead.";
        }

        if (errors.Count == 0)
        {
            var claim = await _access.CanAdministerInstallationAsync(userId, installation.Id, ct);
            if (claim != GitHubInstallationClaim.Confirmed)
            {
                errors["GitHubInstallationId"] = claim switch
                {
                    GitHubInstallationClaim.NotLinked =>
                        "Connect your own GitHub account first, on your account page under Repository access. The toolbox checks with GitHub that you can manage this organisation, and it needs your GitHub account to ask.",
                    GitHubInstallationClaim.LinkUnusable =>
                        "Your GitHub account is no longer connected to the toolbox. Connect it again on your account page under Repository access, then come back and try this.",
                    GitHubInstallationClaim.NotTheirs =>
                        "GitHub does not say you manage that organisation, so nothing was connected. Only someone GitHub lists as an owner there can connect it here - ask one of them to do it, or to make you an owner.",
                    _ =>
                        "The toolbox could not check with GitHub that you manage that organisation, so it did not connect anything. Try again in a minute.",
                };
            }
        }

        if (errors.Count == 0)
        {
            var installationId = installation.Id;
            // Fence category 6 (existence-only uniqueness probe): an installation belongs to
            // one organisation deployment-wide; projects a bool, never a row, and the acting
            // org is excluded so re-connecting your own installation still works.
            var claimedElsewhere = await _db.OrganizationSettings
                .IgnoreQueryFilters()
                .AnyAsync(s => s.GitHubInstallationId == installationId && s.OrganizationId != orgId, ct);
            if (claimedElsewhere)
            {
                _logger.LogWarning(
                    "Org {OrgId} tried to connect GitHub installation {InstallationId}, which another organisation already holds.",
                    orgId, installationId);
                errors["GitHubOrgLogin"] =
                    "That GitHub organisation is already connected to another organisation on this server. Ask whoever runs AL Dev Toolbox if you think that is wrong.";
            }
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);

        var row = await _db.OrganizationSettings.FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        if (row is null)
        {
            row = new OrganizationSettings { OrganizationId = orgId };
            _db.OrganizationSettings.Add(row);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        row.GitHubInstallationId = installation.Id;
        row.GitHubOrgLogin = installation.AccountLogin.Trim();
        row.GitHubInstallationPermissions = SerialisePermissions(installation.Permissions);
        row.GitHubConnectedAt = now;
        row.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        _orgConfig.InvalidateCache(orgId);

        _logger.LogInformation(
            "Org {OrgId} connected GitHub organisation {OrgLogin} (installation {InstallationId}, {PermissionCount} permissions).",
            orgId, row.GitHubOrgLogin, installation.Id, installation.Permissions.Count);
    }

    /// <summary>
    /// Re-reads what GitHub currently says about the connected installation:
    /// the organisation's login (it can be renamed there) and the permissions
    /// it holds. This is how an admin who has just widened the app's access on
    /// GitHub sees the change here, without disconnecting and connecting again.
    /// Leaves <see cref="OrganizationSettings.GitHubConnectedAt"/> alone — the
    /// connection is the same one, so its date must not move.
    ///
    /// <para>Does nothing when the organisation is not connected, or when
    /// <paramref name="installation"/> is a different installation than the one
    /// on file: this refreshes a connection, it never makes one.</para>
    /// </summary>
    public async Task RefreshAsync(GitHubInstallation installation, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var row = await _db.OrganizationSettings.FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        if (row?.GitHubInstallationId is not long current || current != installation.Id)
        {
            return;
        }

        row.GitHubOrgLogin = string.IsNullOrWhiteSpace(installation.AccountLogin)
            ? row.GitHubOrgLogin
            : installation.AccountLogin.Trim();
        row.GitHubInstallationPermissions = SerialisePermissions(installation.Permissions);
        row.UpdatedAt = _clock.GetUtcNow().UtcDateTime;

        await _db.SaveChangesAsync(ct);
        _orgConfig.InvalidateCache(orgId);

        _logger.LogInformation(
            "Org {OrgId} refreshed GitHub installation {InstallationId}; it now holds {PermissionCount} permissions.",
            orgId, installation.Id, installation.Permissions.Count);
    }

    /// <summary>
    /// Clears the connection. The installation itself stays on GitHub — only a
    /// GitHub organisation owner can remove it — so this is reversible from the
    /// same Connect button without a second approval on GitHub's side.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var row = await _db.OrganizationSettings.FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        if (row is null || row.GitHubInstallationId is null)
        {
            _logger.LogInformation("Org {OrgId} asked to disconnect GitHub but was not connected.", orgId);
            return;
        }

        var previous = row.GitHubOrgLogin;
        row.GitHubInstallationId = null;
        row.GitHubOrgLogin = null;
        row.GitHubInstallationPermissions = null;
        row.GitHubConnectedAt = null;
        row.UpdatedAt = _clock.GetUtcNow().UtcDateTime;

        await _db.SaveChangesAsync(ct);
        _orgConfig.InvalidateCache(orgId);

        _logger.LogInformation("Org {OrgId} disconnected GitHub organisation {OrgLogin}.", orgId, previous ?? "<unknown>");
    }

    /// <summary>Stores the permission map as a flat JSON object, key-sorted so diffs stay readable.</summary>
    private static string SerialisePermissions(IReadOnlyDictionary<string, string> permissions) =>
        JsonSerializer.Serialize(new SortedDictionary<string, string>(
            permissions.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal));

    /// <summary>
    /// Reads the stored permission blob back. A null or unparseable column
    /// yields an empty map rather than throwing: the tab must still render for
    /// an admin whose connection predates a shape change.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ParsePermissions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return EmptyPermissions;
        try
        {
            var parsed = JsonSerializer.Deserialize<SortedDictionary<string, string>>(json);
            return parsed is null ? EmptyPermissions : parsed;
        }
        catch (JsonException)
        {
            return EmptyPermissions;
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyPermissions =
        new SortedDictionary<string, string>(StringComparer.Ordinal);
}
