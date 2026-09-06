using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// Owns a project's Business Central SaaS connection: the encrypted S2S secret, the
/// "Test connection" / "Refresh environments" round-trips (token + list environments,
/// flagging missing GDAP), and per-environment company discovery. Access-gated to the
/// project owner / org Admin via <see cref="ProjectAccess"/>; org-scoped through the EF
/// query filter. The secret is encrypted with the Data Protection key ring under
/// <see cref="SecretProtectionPurpose"/> (the SMTP-password / repository-token
/// precedent), written only here, and never returned to callers. See
/// <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed class ProjectConnectionService : IDeliveryTokenSource
{
    /// <summary>Data Protection purpose string for a project's BC S2S client secret.</summary>
    public const string SecretProtectionPurpose = "ALDevToolbox.ProjectBcSecret";

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly BcTokenService _tokens;
    private readonly IBcAdminClient _adminClient;
    private readonly IBcAppManagementClient _apps;
    private readonly IDataProtector _secretProtector;
    private readonly BcPanelCache _panelCache;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProjectConnectionService> _logger;

    public ProjectConnectionService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ProjectAccess access,
        BcTokenService tokens,
        IBcAdminClient adminClient,
        IBcAppManagementClient apps,
        IDataProtectionProvider protectionProvider,
        BcPanelCache panelCache,
        TimeProvider clock,
        ILogger<ProjectConnectionService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _tokens = tokens;
        _adminClient = adminClient;
        _apps = apps;
        _secretProtector = protectionProvider.CreateProtector(SecretProtectionPurpose);
        _panelCache = panelCache;
        _clock = clock;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; BC connection mutation called outside an authenticated request.");

    /// <summary>Presence/verification view of a project's BC connection — never the secret. Null when the project doesn't exist in this org.</summary>
    public async Task<BcConnectionStatus?> GetConnectionAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);
        var p = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == projectId && c.DeletedAt == null)
            .Select(c => new
            {
                c.BcTenantId,
                c.BcClientId,
                HasSecret = c.BcClientSecretEncrypted != null,
                c.BcClientSecretExpiresAt,
                c.BcCredentialsUpdatedAt,
                c.BcTimeZone,
                c.BcConnectionVerifiedAt,
            })
            .FirstOrDefaultAsync(ct);
        if (p is null) return null;

        var configured = p.BcTenantId is not null && !string.IsNullOrEmpty(p.BcClientId) && p.HasSecret;
        return new BcConnectionStatus(
            configured, p.BcTenantId, p.BcClientId, p.HasSecret,
            p.BcClientSecretExpiresAt, p.BcCredentialsUpdatedAt, p.BcTimeZone, p.BcConnectionVerifiedAt);
    }

    /// <summary>
    /// Saves a project's BC connection. The secret follows keep-on-blank semantics: a
    /// non-empty value is encrypted and stored, an empty value leaves the stored secret
    /// untouched. Validates the tenant/client/secret/expiry/timezone and stamps
    /// <c>BcCredentialsUpdatedAt</c>; invalidates the cached token so the next call
    /// re-authenticates. Access-gated.
    /// </summary>
    public async Task SaveConnectionAsync(int projectId, BcConnectionInput input, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var project = await _db.OeProjects
            .FirstOrDefaultAsync(c => c.Id == projectId && c.DeletedAt == null, ct)
            ?? throw Validation("BcTenantId", "This project no longer exists.");
        await _access.EnsureCanManageAsync(projectId, project.CreatedByUserId, ct);

        var errors = new Dictionary<string, string>();

        if (input.TenantId is null || input.TenantId == Guid.Empty)
        {
            errors["BcTenantId"] = "Enter the customer's Microsoft Entra tenant ID (a GUID).";
        }
        var clientId = (input.ClientId ?? string.Empty).Trim();
        if (clientId.Length == 0)
        {
            errors["BcClientId"] = "Enter the app registration's client ID.";
        }

        var newSecret = input.ClientSecret?.Trim();
        var settingSecret = !string.IsNullOrEmpty(newSecret);
        var hasExistingSecret = project.BcClientSecretEncrypted is not null;
        if (!settingSecret && !hasExistingSecret)
        {
            errors["BcClientSecret"] = "Enter the app registration's client secret.";
        }
        if (settingSecret && input.SecretExpiresAt is null)
        {
            errors["BcClientSecretExpiresAt"] = "Enter when the secret expires (Entra shows this when you create it).";
        }

        string? timeZone = null;
        if (!string.IsNullOrWhiteSpace(input.TimeZone))
        {
            timeZone = input.TimeZone.Trim();
            if (!IsValidTimeZone(timeZone))
            {
                errors["BcTimeZone"] = "Use an IANA time zone like 'Europe/Copenhagen'.";
            }
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);

        project.BcTenantId = input.TenantId;
        project.BcClientId = clientId;
        project.BcTimeZone = timeZone;
        if (settingSecret)
        {
            project.BcClientSecretEncrypted = _secretProtector.Protect(newSecret!);
            project.BcClientSecretExpiresAt = DateTime.SpecifyKind(input.SecretExpiresAt!.Value, DateTimeKind.Utc);
        }
        project.BcCredentialsUpdatedAt = DateTime.UtcNow;
        project.UpdatedAt = DateTime.UtcNow;
        // Re-verification is required after a credential change; the previous verify no
        // longer reflects the live creds.
        project.BcConnectionVerifiedAt = null;

        await _db.SaveChangesAsync(ct);
        _tokens.Invalidate(projectId);
        _logger.LogInformation("Saved BC connection for project {ProjectId} (secretChanged={SecretChanged}).", projectId, settingSecret);
    }

    /// <summary>
    /// Runs a "Test connection": acquires a token with the stored credentials and lists
    /// the customer's environments, persisting them (stable upsert) and stamping
    /// <c>BcConnectionVerifiedAt</c> on success. Classifies failures so the UI can render
    /// the GDAP-missing case clearly. Access-gated.
    /// </summary>
    public Task<BcConnectionTestResult> TestConnectionAsync(int projectId, CancellationToken ct = default)
        => FetchAndUpsertEnvironmentsAsync(projectId, markVerified: true, ct);

    /// <summary>Re-fetches and upserts the environment list using the stored credentials. Same round-trip as Test connection. Access-gated.</summary>
    public Task<BcConnectionTestResult> RefreshEnvironmentsAsync(int projectId, CancellationToken ct = default)
        => FetchAndUpsertEnvironmentsAsync(projectId, markVerified: true, ct);

    private async Task<BcConnectionTestResult> FetchAndUpsertEnvironmentsAsync(int projectId, bool markVerified, CancellationToken ct)
    {
        RequireOrganizationId();
        var project = await _db.OeProjects
            .FirstOrDefaultAsync(c => c.Id == projectId && c.DeletedAt == null, ct)
            ?? throw Validation("BcTenantId", "This project no longer exists.");
        await _access.EnsureCanManageAsync(projectId, project.CreatedByUserId, ct);

        return await RefreshEnvironmentsCoreAsync(project, markVerified, ct);
    }

    /// <summary>
    /// Re-reads a project's environments and re-mirrors their Business Central detail,
    /// deliberately <strong>not</strong> access-gated — the precedent is
    /// <see cref="AcquireDeliveryContextAsync"/>. The nightly update sweep runs this from
    /// a background worker where there is no acting user to gate against, under the
    /// project's own org scope so the EF query filter still applies. It never stamps
    /// <c>BcConnectionVerifiedAt</c>: a sweep the consultant never asked for must not
    /// present itself as their "Test connection" result.
    /// <para>
    /// Every caller reaching this from a request must gate first; the public entry points
    /// (<see cref="TestConnectionAsync"/>, <see cref="RefreshEnvironmentsAsync"/>) do.
    /// </para>
    /// </summary>
    public async Task<BcConnectionTestResult> RefreshEnvironmentsUnattendedAsync(int projectId, CancellationToken ct = default)
    {
        var project = await _db.OeProjects
            .FirstOrDefaultAsync(c => c.Id == projectId && c.DeletedAt == null, ct)
            ?? throw Validation("BcTenantId", "This project no longer exists.");

        return await RefreshEnvironmentsCoreAsync(project, markVerified: false, ct);
    }

    /// <summary>
    /// The shared credential-resolve to token to list to upsert to mirror core, with no
    /// access check of its own. Callers decide the gate and whether the round-trip counts
    /// as a verification of the connection.
    /// </summary>
    private async Task<BcConnectionTestResult> RefreshEnvironmentsCoreAsync(OeProject project, bool markVerified, CancellationToken ct)
    {
        var projectId = project.Id;
        var creds = ResolveCredentials(project);
        if (creds is null)
        {
            return new BcConnectionTestResult(BcConnectionResult.AuthFailed, 0,
                "Enter the connection details (tenant, client ID, and secret) first.");
        }

        string token;
        try
        {
            token = await _tokens.GetTokenAsync(projectId, creds.Value.TenantId, creds.Value.ClientId, creds.Value.Secret, forceRefresh: true, ct);
        }
        catch (BcApiException ex)
        {
            _logger.LogWarning("BC test connection: token step failed for project {ProjectId}: {Message}.", projectId, ex.Message);
            return new BcConnectionTestResult(BcConnectionResult.AuthFailed, 0,
                "The credentials were rejected. Check the tenant ID, client ID, and secret, then try again.");
        }

        IReadOnlyList<BcEnvironment> environments;
        try
        {
            environments = await _adminClient.ListEnvironmentsAsync(token, ct);
        }
        // 401 and 403 mean different things here and are fixed in different places, so
        // they get different messages. Entra having issued a token (we got past the step
        // above) tells us nothing about either: Business Central keeps its own list of
        // apps it will talk to.
        catch (BcApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("BC test connection: app not accepted by BC for project {ProjectId}. {Detail}", projectId, ex.Message);
            return new BcConnectionTestResult(BcConnectionResult.AppNotAuthorized, 0,
                "Business Central didn't accept this app. In the Business Central admin center, "
                + "open 'Authorized Microsoft Entra apps', add the client ID above, and grant consent. Then test again.");
        }
        catch (BcApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("BC test connection: environments call denied for project {ProjectId}. {Detail}", projectId, ex.Message);
            return new BcConnectionTestResult(BcConnectionResult.AccessDenied, 0,
                "The app is registered but isn't allowed to list environments. Check that it has the "
                + "AdminCenter.ReadWrite.All permission with admin consent. If this is a customer's tenant you manage "
                + "as a partner, check the delegated admin (GDAP) relationship too.");
        }
        catch (BcApiException ex)
        {
            _logger.LogWarning("BC test connection: environments call failed for project {ProjectId}: {Message}.", projectId, ex.Message);
            return new BcConnectionTestResult(BcConnectionResult.Error, 0,
                "Couldn't list the environments. " + ex.Message);
        }

        await UpsertEnvironmentsAsync(project, environments, ct);
        if (markVerified) project.BcConnectionVerifiedAt = DateTime.UtcNow;
        project.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // After the save, so newly-discovered environments already have rows to mirror
        // onto, and so a failure here can never cost us the environment list itself.
        await MirrorBcEnvironmentDetailsAsync(project, token, ct);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("BC test connection succeeded for project {ProjectId}: {Count} environment(s).", projectId, environments.Count);
        return new BcConnectionTestResult(BcConnectionResult.Success, environments.Count,
            environments.Count == 1 ? "Connected. Found 1 environment." : $"Connected. Found {environments.Count} environments.");
    }

    /// <summary>
    /// The project's fetched environments (the delivery targets). Production first, then
    /// sandboxes, name-ordered within each group: production is the one a consultant is
    /// looking for when something is wrong, and a customer often has several sandboxes
    /// that would otherwise bury it. Read-only.
    /// </summary>
    public async Task<IReadOnlyList<ProjectEnvironmentRow>> ListEnvironmentsAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);
        return await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.ProjectId == projectId)
            // BC reports type as "Production"/"Sandbox"; compare lowered so a casing
            // change on their side can't silently flip the order.
            .OrderBy(e => e.Type.ToLower() == "production" ? 0 : 1)
            .ThenBy(e => e.Name)
            .Select(e => new ProjectEnvironmentRow(
                e.Id, e.Name, e.Type, e.FetchedAt, e.MissingSince,
                e.UpdateWindowStart, e.UpdateWindowEnd,
                e.Status,
                e.AppSourceAppsUpdateCadence,
                e.BcUpdateWindowStart, e.BcUpdateWindowEnd, e.BcUpdateWindowTimeZoneIana, e.BcUpdateWindowFetchedAt,
                e.Version, e.WebClientLoginUrl))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Everything the environment panel shows, fetched live: installed apps, available
    /// Marketplace app updates, scheduled per-tenant installs, and the platform updates
    /// coming to the environment. Access-gated.
    /// <para>
    /// <b>Cached for <see cref="BcPanelCache.Ttl"/>.</b> These are four reads against
    /// Business Central, and re-issuing them every time a consultant expands a row is
    /// traffic Microsoft's API does not need — especially as we honour no throttle. A
    /// window this short keeps the panel's promise of "what is true right now" while
    /// collapsing a working session's repeated opens into one fetch. Anything we write
    /// ourselves invalidates the entry (see <see cref="BcPanelCache.Invalidate"/>), so a
    /// consultant never reads a stale answer caused by their own action; a change made
    /// directly in Business Central is picked up by <paramref name="forceRefresh"/>.
    /// </para>
    /// <para>
    /// Each section fails on its own. One endpoint being denied — the app-management
    /// reads and the platform-update read are different permissions in practice — must
    /// not blank the other three, so a failure is carried as that section's message and
    /// the rest still render.
    /// </para>
    /// </summary>
    /// <param name="forceRefresh">Bypass the cache and re-read — what the panel's Refresh does.</param>
    public async Task<BcEnvironmentPanel> GetEnvironmentPanelAsync(
        int projectId, int environmentId, bool forceRefresh = false, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var project = await _db.OeProjects.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == projectId && c.DeletedAt == null, ct)
            ?? throw Validation("Environment", "This project no longer exists.");
        await _access.EnsureCanManageAsync(projectId, project.CreatedByUserId, ct);

        // Only now, with the organisation and access checks passed, may we look at the
        // cache: it is keyed by ids alone and knows nothing about who is allowed to read
        // them. Which apps are ours is re-read either way — that comes from our own
        // database, costs one cheap query, and means a delivery made since the cached
        // read still shows up as ours.
        if (!forceRefresh && _panelCache.Get(projectId, environmentId) is { } cached)
        {
            return cached with
            {
                ReleasedAppIds = await ReleasedAppIdsAsync(projectId, cached.EnvironmentName, ct),
            };
        }

        var env = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == environmentId && e.ProjectId == projectId)
            .Select(e => new { e.Name, e.ApplicationFamily })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("Environment", "That environment no longer exists. Refresh the list and try again.");

        var creds = ResolveCredentials(project)
            ?? throw Validation("Environment", "Enter the Business Central connection details first.");

        string token;
        try
        {
            token = await _tokens.GetTokenAsync(projectId, creds.TenantId, creds.ClientId, creds.Secret, ct: ct);
        }
        catch (BcApiException)
        {
            throw Validation("Environment", "The credentials were rejected. Re-enter them and test the connection again.");
        }

        var family = string.IsNullOrWhiteSpace(env.ApplicationFamily)
            ? BcConstants.DefaultApplicationFamily
            : env.ApplicationFamily;

        // The four reads don't depend on each other, so they go out together and the panel
        // costs one round trip's wait rather than four. Nothing here touches the DbContext
        // — its work is done above and resumes below — so the scoped context is never used
        // concurrently. Task.WhenAll first so a non-BcApiException from one read can't
        // leave the other three unobserved.
        var installedTask = ReadSectionAsync(() => _apps.ListInstalledAppsAsync(token, family, env.Name, ct),
            "the installed apps", env.Name);
        var updatesTask = ReadSectionAsync(() => _apps.ListAvailableUpdatesAsync(token, family, env.Name, ct),
            "the available Marketplace app updates", env.Name);
        var scheduledTask = ReadSectionAsync(() => _apps.ListScheduledPteOperationsAsync(token, family, env.Name, ct),
            "the scheduled installs", env.Name);
        var platformTask = ReadSectionAsync(() => _adminClient.ListEnvironmentUpdatesAsync(token, family, env.Name, ct),
            "the Business Central updates", env.Name);
        await Task.WhenAll(installedTask, updatesTask, scheduledTask, platformTask);

        var installed = await installedTask;
        var updates = await updatesTask;
        var scheduled = await scheduledTask;
        var platform = await platformTask;

        var panel = new BcEnvironmentPanel(
            env.Name,
            await ReleasedAppIdsAsync(projectId, env.Name, ct),
            installed.Items, installed.Error,
            updates.Items, updates.Error,
            scheduled.Items, scheduled.Error,
            platform.Items, platform.Error,
            _clock.GetUtcNow().UtcDateTime);

        _panelCache.Set(projectId, environmentId, panel);
        return panel;
    }

    /// <summary>
    /// Which of the apps in an environment this toolbox has actually released there.
    /// Best-effort by app id, from the delivery history: enough to tell a consultant
    /// "this pending install is one of yours" instead of leaving them to recognise a
    /// publisher name.
    /// </summary>
    private async Task<IReadOnlySet<Guid>> ReleasedAppIdsAsync(
        int projectId, string environmentName, CancellationToken ct)
    {
        var ids = await _db.OeProjectDeliveryResults.AsNoTracking()
            .Where(r => r.AppId != null
                        && r.ProjectDelivery!.ProjectId == projectId
                        && r.ProjectDelivery.EnvironmentName == environmentName)
            .Select(r => r.AppId!)
            .Distinct()
            .ToListAsync(ct);

        return ids
            .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToHashSet();
    }

    /// <summary>
    /// Runs one panel read, turning a refusal into a message for that section instead of
    /// an exception that would take the whole panel down with it.
    /// </summary>
    private async Task<(IReadOnlyList<T> Items, string? Error)> ReadSectionAsync<T>(
        Func<Task<IReadOnlyList<T>>> read, string what, string environmentName)
    {
        try
        {
            return (await read().ConfigureAwait(false), null);
        }
        catch (BcApiException ex)
        {
            _logger.LogWarning("Couldn't read {What} for environment {Environment}: {Message}.",
                what, environmentName, ex.Message);
            return (Array.Empty<T>(), $"Couldn't read {what} from Business Central. {ex.Message}");
        }
    }

    /// <summary>
    /// Which permission an environment write asks for. The two axes are deliberately
    /// separate (see <see cref="ProjectAccess.CanManageEnvironmentUpdatesAsync"/>):
    /// managing a project does not grant the update-ops flag, and holding the flag does
    /// not make somebody a project manager.
    /// </summary>
    private enum EnvironmentGate
    {
        /// <summary>Owner / org Admin / assigned-team manager — everything on the BC tab.</summary>
        Manage,

        /// <summary>The environment-updates flag only — the fleet actions from issue #657.</summary>
        UpdateOps,

        /// <summary>Either will do: a project manager and an update-ops holder both have a reason to pick the next version.</summary>
        ManageOrUpdateOps,
    }

    /// <summary>
    /// Resolves the token and family for one environment, after checking the caller passes
    /// <paramref name="gate"/>. Every 5b write goes through here, so the access check and
    /// the "connection not set up" message live in one place.
    /// </summary>
    private async Task<(string Token, string Family, string Name, int Id)> ResolveEnvironmentAsync(
        int projectId, int environmentId, CancellationToken ct, EnvironmentGate gate = EnvironmentGate.Manage)
    {
        RequireOrganizationId();
        var project = await _db.OeProjects.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == projectId && c.DeletedAt == null, ct)
            ?? throw Validation("Environment", "This project no longer exists.");
        await EnsureGateAsync(gate, projectId, project.CreatedByUserId, ct);

        var env = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == environmentId && e.ProjectId == projectId)
            .Select(e => new { e.Id, e.Name, e.ApplicationFamily })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("Environment", "That environment no longer exists. Refresh the list and try again.");

        var creds = ResolveCredentials(project)
            ?? throw Validation("Environment", "Enter the Business Central connection details first.");

        string token;
        try
        {
            token = await _tokens.GetTokenAsync(projectId, creds.TenantId, creds.ClientId, creds.Secret, ct: ct);
        }
        catch (BcApiException)
        {
            throw Validation("Environment", "The credentials were rejected. Re-enter them and test the connection again.");
        }

        var family = string.IsNullOrWhiteSpace(env.ApplicationFamily)
            ? BcConstants.DefaultApplicationFamily
            : env.ApplicationFamily;
        return (token, family, env.Name, env.Id);
    }

    /// <summary>
    /// Runs one of the three access checks. The "either" case tries the project-manage
    /// axis first and falls back to the update-ops flag, so a refusal names both ways in.
    /// </summary>
    private async Task EnsureGateAsync(EnvironmentGate gate, int projectId, int? createdByUserId, CancellationToken ct)
    {
        switch (gate)
        {
            case EnvironmentGate.Manage:
                await _access.EnsureCanManageAsync(projectId, createdByUserId, ct);
                break;
            case EnvironmentGate.UpdateOps:
                await _access.EnsureCanManageEnvironmentUpdatesAsync(projectId, ct);
                break;
            default:
                if (await _access.CanManageAsync(projectId, createdByUserId, ct)) break;
                if (await _access.CanManageEnvironmentUpdatesAsync(projectId, ct)) break;
                throw new ProjectAccessDeniedException(
                    "You need to manage this project, or hold permission to manage environment updates for one of its teams.");
        }
    }

    /// <summary>
    /// Sets how often Marketplace apps update on the environment, then refreshes the
    /// cached column so the page agrees with the tenant. The row write is what puts this
    /// in the audit log — see <c>AuditInterceptor.EnvironmentSettingColumns</c>.
    /// </summary>
    public async Task SetAppUpdateCadenceAsync(int projectId, int environmentId, string cadence, CancellationToken ct = default)
    {
        if (BcAppUpdateCadence.Normalize(cadence) is not { } value)
        {
            throw Validation("Cadence", "Choose how often Marketplace apps should update.");
        }

        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct);
        try
        {
            await _adminClient.SetAppUpdateCadenceAsync(env.Token, env.Family, env.Name, value, ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("Cadence", ex.Message);
        }

        var row = await _db.OeProjectEnvironments.FirstAsync(e => e.Id == env.Id, ct);
        row.AppSourceAppsUpdateCadence = value;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Reads whether Microsoft 365 licence access is on. Null when Business Central
    /// doesn't say (an environment too old to support it answers nothing useful).
    /// </summary>
    public async Task<bool?> GetM365AccessAsync(int projectId, int environmentId, CancellationToken ct = default)
    {
        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct);
        try
        {
            return await _adminClient.GetM365AccessAsync(env.Token, env.Family, env.Name, ct);
        }
        catch (BcApiException)
        {
            return null;
        }
    }

    /// <summary>
    /// Turns Microsoft 365 licence access on or off. Changes who can sign in to the
    /// customer's tenant and touches no row of ours, so it is recorded in the log rather
    /// than the audit trail — see <c>.design/saas-delivery.md</c>.
    /// </summary>
    public async Task SetM365AccessAsync(int projectId, int environmentId, bool enabled, CancellationToken ct = default)
    {
        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct);
        try
        {
            await _adminClient.SetM365AccessAsync(env.Token, env.Family, env.Name, enabled, ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("M365Access", ex.Message);
        }

        _logger.LogInformation(
            "User {UserId} set Microsoft 365 licence access to {Enabled} on {Environment} (project {ProjectId}).",
            _orgContext.CurrentUserId, enabled, env.Name, projectId);
    }

    /// <summary>
    /// Selects the platform version the environment updates to next — a reschedule of the
    /// customer's Business Central upgrade. Refuses a version the environment doesn't
    /// report as available, so a stale page can't schedule something Microsoft hasn't
    /// released. Touches no row of ours, so it is recorded in the log.
    /// <para>
    /// Open to a project manager <em>or</em> someone holding the environment-updates flag
    /// on one of the project's teams: picking the version a customer moves to is the same
    /// job as moving its date, which the upgrade team owns (issue #657).
    /// </para>
    /// </summary>
    public async Task SelectTargetVersionAsync(
        int projectId, int environmentId, string targetVersion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            throw Validation("TargetVersion", "Choose the version to update to.");
        }

        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct, EnvironmentGate.ManageOrUpdateOps);

        IReadOnlyList<BcEnvironmentUpdate> updates;
        try
        {
            updates = await _adminClient.ListEnvironmentUpdatesAsync(env.Token, env.Family, env.Name, ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("TargetVersion", "Couldn't read the versions available for this environment. " + ex.Message);
        }

        var chosen = updates.FirstOrDefault(u =>
            string.Equals(u.TargetVersion, targetVersion.Trim(), StringComparison.OrdinalIgnoreCase));
        if (chosen is null || !chosen.Available)
        {
            throw Validation("TargetVersion",
                $"Business Central {targetVersion} isn't available for {env.Name} right now. Reopen the panel to see what is.");
        }

        try
        {
            await _adminClient.SelectTargetVersionAsync(
                env.Token, env.Family, env.Name, chosen.TargetVersion, chosen.TargetVersionType, ct: ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("TargetVersion", ex.Message);
        }

        // We just changed what the panel's updates section says, so nobody should be
        // shown the answer we cached before this call.
        _panelCache.Invalidate(projectId, environmentId);

        _logger.LogInformation(
            "User {UserId} scheduled Business Central {Version} as the next update for {Environment} (project {ProjectId}).",
            _orgContext.CurrentUserId, chosen.TargetVersion, env.Name, projectId);
    }

    /// <summary>
    /// Moves the environment's next platform update to the latest date Microsoft still
    /// allows — the routine sweep the upgrade team runs across every customer before a
    /// release lands (issue #657). Gated on the environment-updates flag, not on managing
    /// the project. Refuses when there is no update to move, when Business Central gives
    /// the update no latest date, and when the date is already there, each with a message
    /// a fleet page can show against the row.
    /// </summary>
    public async Task PushUpdateDateToLatestAsync(int projectId, int environmentId, CancellationToken ct = default)
    {
        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct, EnvironmentGate.UpdateOps);
        var next = await ReadNextUpdateAsync(env, ct)
            ?? throw Validation("Update", "No update is available to reschedule.");

        if (next.LatestSelectableDateTime is not { } latest)
        {
            throw Validation("Update", "Business Central hasn't given this update a last possible date, so it can't be moved.");
        }
        if (next.SelectedDateTime == latest)
        {
            throw Validation("Update", "This update's date is already the latest Microsoft allows.");
        }

        await WriteUpdateScheduleAsync(env, next, latest, ignoreUpdateWindow: null, ct);
        await RecordUpdateActionAsync(
            projectId, env, next, "Moved the update date out to the latest Business Central allows", ct);
        _panelCache.Invalidate(projectId, environmentId);

        _logger.LogInformation(
            "User {UserId} pushed the Business Central {Version} update on {Environment} (project {ProjectId}) out to {SelectedDateTime}.",
            _orgContext.CurrentUserId, next.TargetVersion, env.Name, projectId, latest);
    }

    /// <summary>
    /// Starts the environment's next platform update as soon as Business Central will take
    /// it: the date is set to now and the environment's update window is ignored, which is
    /// what a customer who has agreed a slot is asking for. This is the only operation that
    /// ever ignores the window. Gated on the environment-updates flag; refuses when there
    /// is no update to run.
    /// </summary>
    public async Task RunUpdateNowAsync(int projectId, int environmentId, CancellationToken ct = default)
    {
        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct, EnvironmentGate.UpdateOps);
        var next = await ReadNextUpdateAsync(env, ct)
            ?? throw Validation("Update", "No update is available to run.");

        var now = DateTimeOffset.UtcNow;
        await WriteUpdateScheduleAsync(env, next, now, ignoreUpdateWindow: true, ct);
        await RecordUpdateActionAsync(
            projectId, env, next, "Started the update now, ignoring the environment's update window", ct);
        _panelCache.Invalidate(projectId, environmentId);

        _logger.LogInformation(
            "User {UserId} started the Business Central {Version} update on {Environment} (project {ProjectId}) at {SelectedDateTime}, ignoring the update window.",
            _orgContext.CurrentUserId, next.TargetVersion, env.Name, projectId, now);
    }

    /// <summary>
    /// Reads the environment's updates live and picks the one a date write acts on — the
    /// same rule the mirror caches, so the fleet page and the write agree on which update
    /// "the next update" is. Null when the environment has nothing on offer.
    /// </summary>
    private async Task<BcEnvironmentUpdate?> ReadNextUpdateAsync(
        (string Token, string Family, string Name, int Id) env, CancellationToken ct)
    {
        try
        {
            var updates = await _adminClient.ListEnvironmentUpdatesAsync(env.Token, env.Family, env.Name, ct);
            return PickNextUpdate(updates);
        }
        catch (BcApiException ex)
        {
            throw Validation("Update", "Couldn't read the updates for this environment. " + ex.Message);
        }
    }

    /// <summary>
    /// Sends the date write and re-mirrors the row from a fresh read, so the fleet page
    /// shows the new date without waiting for the nightly sweep. The PATCH also selects
    /// the update, which matters when the picked one was merely available: setting a date
    /// on it is the customer choosing it.
    /// </summary>
    private async Task WriteUpdateScheduleAsync(
        (string Token, string Family, string Name, int Id) env,
        BcEnvironmentUpdate update,
        DateTimeOffset selectedDateTime,
        bool? ignoreUpdateWindow,
        CancellationToken ct)
    {
        try
        {
            await _adminClient.SelectTargetVersionAsync(
                env.Token, env.Family, env.Name, update.TargetVersion, update.TargetVersionType,
                selectedDateTime, ignoreUpdateWindow, ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("Update", ex.Message);
        }

        // Re-read rather than assume: Business Central decides what it actually stored,
        // and a mirror that says what we asked for would be a guess. A failure here loses
        // the freshness, never the write.
        try
        {
            var updates = await _adminClient.ListEnvironmentUpdatesAsync(env.Token, env.Family, env.Name, ct);
            var row = await _db.OeProjectEnvironments.FirstOrDefaultAsync(e => e.Id == env.Id, ct);
            if (row is not null)
            {
                ApplyNextUpdate(row, PickNextUpdate(updates));
                await _db.SaveChangesAsync(ct);
            }
        }
        catch (BcApiException ex)
        {
            _logger.LogWarning(
                "The update date on {Environment} was changed, but re-reading it failed: {Message}. The cached row stays stale until the next refresh.",
                env.Name, ex.Message);
        }
    }

    /// <summary>
    /// Records one fleet update action in the audit log. These two writes act on a
    /// <em>customer's production tenant</em> and touch no row of ours that the
    /// interceptor watches — the re-mirror afterwards is deliberately outside
    /// <c>AuditInterceptor.EnvironmentSettingColumns</c>, because the nightly sweep
    /// writes the same columns and would otherwise fill the log with rows nobody made.
    /// So the entry is written here, explicitly, and it is the only place in the
    /// application that writes to <c>audit_log</c> directly. See issue #657.
    ///
    /// <para>The snapshot keeps the log's "state before the change" contract: it is what
    /// the update looked like when we read it, plus a plain-words <c>Action</c> naming
    /// which of the two writes this was — the audit model records rows changing, and
    /// these are events, so the event has to be spelled out in the row itself. Two of
    /// these rows on one environment diff against each other cleanly, which is what the
    /// audit diff page reads.</para>
    ///
    /// <para>The actor is resolved from the database rather than from claims because
    /// this runs inside a Blazor circuit, where the interceptor's own
    /// <c>HttpContext</c> lookup has nothing to read.</para>
    /// </summary>
    private async Task RecordUpdateActionAsync(
        int projectId,
        (string Token, string Family, string Name, int Id) env,
        BcEnvironmentUpdate update,
        string action,
        CancellationToken ct)
    {
        var changedBy = await ResolveActorAsync(ct);
        var projectName = await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(ct);

        var snapshot = new Dictionary<string, object?>
        {
            ["Action"] = action,
            ["Project"] = projectName,
            ["Name"] = env.Name,
            ["UpdateVersion"] = update.TargetVersion,
            ["UpdateDate"] = update.SelectedDateTime?.UtcDateTime,
            ["LatestPossibleDate"] = update.LatestSelectableDateTime?.UtcDateTime,
            ["IgnoresUpdateWindow"] = update.IgnoreUpdateWindow,
        };

        _db.AuditLog.Add(new AuditLogEntry
        {
            Timestamp = DateTime.UtcNow,
            ChangedBy = changedBy,
            ChangedByUserId = _orgContext.CurrentUserId,
            OrganizationId = _orgContext.CurrentOrganizationId,
            EntityType = AuditEntityType.ProjectEnvironment,
            EntityId = env.Id,
            Action = AuditAction.Updated,
            EntityName = env.Name,
            SnapshotJson = JsonSerializer.Serialize(snapshot, PersistenceJson.Options),
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The acting user in the audit log's <c>"display name &lt;email&gt;"</c> form.
    /// Cached for the scope: a bulk run calls this once per row. The lookup itself is
    /// <see cref="AuditActor"/>, shared with the upgrade-action feed so one environment's
    /// history names a person the same way whichever route wrote the row.
    /// </summary>
    private async Task<string> ResolveActorAsync(CancellationToken ct) =>
        _actor ??= await AuditActor.ResolveAsync(_db, _orgContext.CurrentUserId, ct);

    private string? _actor;

    /// <summary>
    /// Cancels one per-tenant extension version that Business Central has scheduled but
    /// not yet installed — the action that makes a handed-off delivery undoable.
    /// Access-gated. This permanently removes the uploaded package from Business Central,
    /// so releasing that version again means uploading it again.
    /// </summary>
    public async Task CancelScheduledInstallAsync(
        int projectId, int environmentId, Guid appId, string targetVersion, string scheduleKind, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var project = await _db.OeProjects.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == projectId && c.DeletedAt == null, ct)
            ?? throw Validation("Environment", "This project no longer exists.");
        await _access.EnsureCanManageAsync(projectId, project.CreatedByUserId, ct);

        var env = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == environmentId && e.ProjectId == projectId)
            .Select(e => new { e.Name, e.ApplicationFamily })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("Environment", "That environment no longer exists.");

        var creds = ResolveCredentials(project)
            ?? throw Validation("Environment", "Enter the Business Central connection details first.");

        string token;
        try
        {
            token = await _tokens.GetTokenAsync(projectId, creds.TenantId, creds.ClientId, creds.Secret, ct: ct);
        }
        catch (BcApiException)
        {
            throw Validation("Environment", "The credentials were rejected. Re-enter them and test the connection again.");
        }

        var family = string.IsNullOrWhiteSpace(env.ApplicationFamily)
            ? BcConstants.DefaultApplicationFamily
            : env.ApplicationFamily;

        try
        {
            await _apps.RemoveScheduledPteVersionAsync(token, family, env.Name, appId, targetVersion, scheduleKind, ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("Environment", "Business Central didn't cancel the scheduled install. " + ex.Message);
        }

        _panelCache.Invalidate(projectId, environmentId);

        _logger.LogInformation(
            "Cancelled the scheduled install of app {AppId} version {Version} ({ScheduleKind}) on {Environment} (project {ProjectId}).",
            appId, targetVersion, scheduleKind, env.Name, projectId);
    }

    /// <summary>
    /// Sets or clears an environment's recurring update window. Pass both
    /// <paramref name="start"/> and <paramref name="end"/> to set it, or both null to
    /// clear it ("any time"); passing only one is a validation error. Interpreted in the
    /// project's timezone. Access-gated; survives a Refresh (the discovery upsert only
    /// touches fetched fields). See <c>.design/saas-delivery.md</c> ("Update window").
    /// </summary>
    public async Task SetUpdateWindowAsync(int projectId, int environmentId, TimeOnly? start, TimeOnly? end, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var ownerId = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == projectId)
            .Select(c => c.CreatedByUserId)
            .FirstOrDefaultAsync(ct);
        await _access.EnsureCanManageAsync(projectId, ownerId, ct);

        if (start is null != (end is null))
        {
            throw Validation("UpdateWindow", "Set both a start and an end time for the window, or clear both for 'any time'.");
        }

        var env = await _db.OeProjectEnvironments
            .FirstOrDefaultAsync(e => e.Id == environmentId && e.ProjectId == projectId, ct)
            ?? throw Validation("Environment", "That environment no longer exists.");

        env.UpdateWindowStart = start;
        env.UpdateWindowEnd = end;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Set update window {Start}-{End} for environment {EnvId} (project {ProjectId}).",
            start, end, environmentId, projectId);
    }

    /// <summary>
    /// Resolves a project's BC credentials and returns the token plus tenant the
    /// delivery worker publishes with. Deliberately <strong>not</strong> access-gated:
    /// it's called from the delivery worker <em>after</em> the release was authorised at
    /// creation, under the triggering user's captured identity (so the org query filter
    /// still scopes the project). The secret never leaves this service. Throws
    /// <see cref="BcApiException"/> with a clear, secret-free message when the connection
    /// isn't configured, the key ring can't decrypt the secret, the secret has expired,
    /// or Entra rejects the credentials — the worker records that as the failure reason.
    /// See <c>.design/saas-delivery.md</c> ("Authentication", "Expired-secret behaviour").
    /// </summary>
    public async Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, CancellationToken ct = default)
    {
        var project = await _db.OeProjects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId && p.DeletedAt == null, ct)
            ?? throw new BcApiException(null, "This project no longer exists.");

        var creds = ResolveCredentials(project)
            ?? throw new BcApiException(null,
                "The Business Central connection isn't set up (or its secret can't be decrypted). Re-enter it on the project's Business Central page.");

        if (project.BcClientSecretExpiresAt is { } expiry && expiry <= DateTime.UtcNow)
        {
            throw new BcApiException(null,
                "The Business Central client secret has expired. Rotate it in Entra and re-enter it before releasing.");
        }

        var token = await _tokens.GetTokenAsync(projectId, creds.TenantId, creds.ClientId, creds.Secret, ct: ct)
            .ConfigureAwait(false);
        return new BcDeliveryContext(token, creds.TenantId);
    }

    /// <summary>
    /// Stable upsert of the fetched environments onto the project's tracked
    /// <see cref="OeProject.Environments"/>: match by name (preserving each row's id and
    /// picked company), add new ones, and stamp <c>MissingSince</c> on any that the
    /// fetch no longer returns rather than deleting them — so a release pipeline's FK
    /// never dangles. Assumes the caller saves.
    /// </summary>
    private async Task UpsertEnvironmentsAsync(OeProject project, IReadOnlyList<BcEnvironment> fetched, CancellationToken ct)
    {
        var existing = await _db.OeProjectEnvironments
            .Where(e => e.ProjectId == project.Id)
            .ToListAsync(ct);
        var byName = existing.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var env in fetched)
        {
            seen.Add(env.Name);
            if (byName.TryGetValue(env.Name, out var row))
            {
                ApplyFetched(row, env, now);
                row.MissingSince = null; // back if it had vanished
            }
            else
            {
                var row2 = new OeProjectEnvironment
                {
                    OrganizationId = project.OrganizationId,
                    ProjectId = project.Id,
                    Name = env.Name,
                };
                ApplyFetched(row2, env, now);
                _db.OeProjectEnvironments.Add(row2);
            }
        }

        foreach (var row in existing)
        {
            if (!seen.Contains(row.Name) && row.MissingSince is null)
            {
                row.MissingSince = now;
            }
        }
    }

    /// <summary>
    /// Mirrors each environment's <em>Microsoft</em> update window and its next platform
    /// update onto its row.
    /// <para>
    /// This is two extra calls per environment on top of the single list call — twenty
    /// sandboxes make a Refresh forty-one requests instead of one. It rides the Refresh
    /// anyway because the alternative (fetching when a panel opens) would put a network
    /// round trip in the way of every glance at the table, and the window changes about
    /// as often as the environment list does. If it ever bites, this is the method to
    /// make lazy; the stamped <c>BcUpdateWindowFetchedAt</c> already lets the UI say how
    /// old the answer is.
    /// </para>
    /// <para>
    /// A failure for one environment must not fail the Refresh: the environment list is
    /// the point of the operation, and this is context beside it. On failure the previous
    /// answer and its age are left alone rather than blanked, so the table degrades to
    /// stale rather than to empty. The two mirrors fail independently — a denied updates
    /// read still leaves a freshly-read window.
    /// </para>
    /// </summary>
    private async Task MirrorBcEnvironmentDetailsAsync(OeProject project, string token, CancellationToken ct)
    {
        var rows = await _db.OeProjectEnvironments
            .Where(e => e.ProjectId == project.Id && e.MissingSince == null)
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var settings = await _adminClient.GetUpdateSettingsAsync(token, row.ApplicationFamily, row.Name, ct);
                row.BcUpdateWindowStart = settings?.StartTime;
                row.BcUpdateWindowEnd = settings?.EndTime;
                row.BcUpdateWindowTimeZoneId = settings?.WindowsTimeZoneId;
                row.BcUpdateWindowTimeZoneIana = BcUpdateWindow.ToIana(settings?.WindowsTimeZoneId);
                row.BcUpdateWindowFetchedAt = DateTime.UtcNow;
            }
            catch (BcApiException ex)
            {
                _logger.LogWarning(
                    "Couldn't read the Business Central update window for {Environment} (project {ProjectId}): {Message}.",
                    row.Name, project.Id, ex.Message);
            }

            ct.ThrowIfCancellationRequested();
            try
            {
                var updates = await _adminClient.ListEnvironmentUpdatesAsync(token, row.ApplicationFamily, row.Name, ct);
                ApplyNextUpdate(row, PickNextUpdate(updates));
            }
            catch (BcApiException ex)
            {
                _logger.LogWarning(
                    "Couldn't read the Business Central platform updates for {Environment} (project {ProjectId}): {Message}.",
                    row.Name, project.Id, ex.Message);
            }
        }
    }

    /// <summary>
    /// The one update out of an environment's list worth caching: the <em>selected</em>
    /// one when the customer has picked a slot (that is the answer to "when does this
    /// customer move?"), else the newest one they could still pick, else nothing. An
    /// unavailable, unselected version is a Microsoft roadmap entry with no date on it,
    /// so it is not a candidate.
    /// </summary>
    internal static BcEnvironmentUpdate? PickNextUpdate(IReadOnlyList<BcEnvironmentUpdate> updates)
    {
        var selected = updates.FirstOrDefault(u => u.Selected);
        if (selected is not null) return selected;

        BcEnvironmentUpdate? newest = null;
        foreach (var candidate in updates)
        {
            if (!candidate.Available) continue;
            if (newest is null || CompareVersions(candidate.TargetVersion, newest.TargetVersion) > 0)
            {
                newest = candidate;
            }
        }
        return newest;
    }

    /// <summary>
    /// Orders two BC platform versions by numeric segment, because a string compare puts
    /// "10.1" before "9.2" and would quietly pick last year's update as the newest.
    /// A segment that isn't a number sorts as 0 rather than throwing — Microsoft's
    /// version strings are theirs to change.
    /// </summary>
    private static int CompareVersions(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length && int.TryParse(a[i], out var xv) ? xv : 0;
            var y = i < b.Length && int.TryParse(b[i], out var yv) ? yv : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    /// <summary>
    /// Writes the picked update onto the row, clearing all six value columns when there
    /// is nothing to show. Either way <c>BcNextUpdateFetchedAt</c> is stamped: an empty
    /// list is a successful read that says "nothing is scheduled", which is a different
    /// fact from "we never asked".
    /// </summary>
    private static void ApplyNextUpdate(OeProjectEnvironment row, BcEnvironmentUpdate? update)
    {
        row.BcNextUpdateVersion = update?.TargetVersion;
        row.BcNextUpdateType = update?.TargetVersionType;
        row.BcNextUpdateStatus = update?.UpdateStatus;
        row.BcNextUpdateDate = update?.SelectedDateTime?.UtcDateTime;
        row.BcNextUpdateLatestDate = update?.LatestSelectableDateTime?.UtcDateTime;
        row.BcNextUpdateIgnoresWindow = update is null ? null : update.IgnoreUpdateWindow;
        row.BcNextUpdateFetchedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Copies the fetched detail from one API record onto a row. Only fields the API
    /// reports are touched, so the user's own settings on the row (the delivery window)
    /// survive a refresh. <c>geoName</c> is absent from the by-name
    /// response, so a null there leaves the cached value in place rather than erasing it.
    /// </summary>
    private static void ApplyFetched(OeProjectEnvironment row, BcEnvironment env, DateTime now)
    {
        row.Type = env.Type;
        row.FriendlyName = env.FriendlyName;
        row.ApplicationFamily = env.ApplicationFamily;
        row.Status = env.Status;
        row.StatusFetchedAt = now;
        row.CountryCode = env.CountryCode;
        row.AadTenantId = env.AadTenantId;
        row.WebClientLoginUrl = env.WebClientLoginUrl;
        row.LocationName = env.LocationName;
        row.GeoName = env.GeoName ?? row.GeoName;
        row.RingName = env.RingName;
        row.AppSourceAppsUpdateCadence = env.AppSourceAppsUpdateCadence;
        row.Version = env.Version;
        row.GracePeriodStartDate = env.GracePeriodStartDate;
        row.EnforcedUpdatePeriodStartDate = env.EnforcedUpdatePeriodStartDate;
        row.SoftDeletedOn = env.SoftDeletedOn;
        row.HardDeletePendingOn = env.HardDeletePendingOn;
        row.DeleteReason = env.DeleteReason;
        row.FetchedAt = now;
    }

    /// <summary>Decrypts the stored credentials, or null when not fully configured / the key ring can't decrypt the secret.</summary>
    private (Guid TenantId, string ClientId, string Secret)? ResolveCredentials(OeProject project)
    {
        if (project.BcTenantId is null || project.BcTenantId == Guid.Empty) return null;
        if (string.IsNullOrEmpty(project.BcClientId)) return null;
        if (string.IsNullOrEmpty(project.BcClientSecretEncrypted)) return null;

        try
        {
            var secret = _secretProtector.Unprotect(project.BcClientSecretEncrypted);
            return (project.BcTenantId.Value, project.BcClientId, secret);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            _logger.LogError(ex, "Could not decrypt the BC client secret for project {ProjectId}; it must be re-entered.", project.Id);
            return null;
        }
    }

    private static bool IsValidTimeZone(string ianaId)
    {
        try
        {
            // .NET on Linux resolves IANA ids natively; on Windows it falls back via ICU.
            TimeZoneInfo.FindSystemTimeZoneById(ianaId);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static PlanValidationException Validation(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });
}

/// <summary>Form-post shape for a project's BC connection. The secret is keep-on-blank (empty leaves the stored one).</summary>
public sealed record BcConnectionInput(
    Guid? TenantId,
    string? ClientId,
    string? ClientSecret,
    DateTime? SecretExpiresAt,
    string? TimeZone);

/// <summary>Presence/verification view of a project's BC connection. Never carries the secret.</summary>
public sealed record BcConnectionStatus(
    bool IsConfigured,
    Guid? TenantId,
    string? ClientId,
    bool HasSecret,
    DateTime? SecretExpiresAt,
    DateTime? CredentialsUpdatedAt,
    string? TimeZone,
    DateTime? VerifiedAt);

/// <summary>One fetched BC environment — the project detail page's environment row.</summary>
public sealed record ProjectEnvironmentRow(
    int Id,
    string Name,
    string Type,
    DateTime FetchedAt,
    DateTime? MissingSince,
    TimeOnly? UpdateWindowStart,
    TimeOnly? UpdateWindowEnd,
    /// <summary>Lifecycle status from the last fetch, verbatim. Null on rows fetched before it was captured.</summary>
    string? Status,
    /// <summary>How often Marketplace apps update on the environment (a <see cref="BcAppUpdateCadence"/> value).</summary>
    string? AppSourceAppsUpdateCadence,
    /// <summary>Start of Microsoft's platform-update window, in <see cref="BcUpdateWindowTimeZoneIana"/>. Not the delivery window.</summary>
    TimeOnly? BcUpdateWindowStart,
    /// <summary>End of Microsoft's platform-update window.</summary>
    TimeOnly? BcUpdateWindowEnd,
    /// <summary>IANA form of the zone Microsoft's window is expressed in; null when the Windows id had no mapping.</summary>
    string? BcUpdateWindowTimeZoneIana,
    /// <summary>When the Microsoft window was last read successfully.</summary>
    DateTime? BcUpdateWindowFetchedAt,
    /// <summary>The environment's Business Central version from the last fetch.</summary>
    string? Version,
    /// <summary>Deep link into the environment's web client, for "Open in Business Central".</summary>
    string? WebClientLoginUrl);

/// <summary>
/// A live snapshot of one Business Central environment, for the panel on the project's
/// Business Central tab. Nothing here is persisted — it answers "what is on this
/// environment and what is about to change" at the moment the panel was opened.
/// Each section carries its own error so one refusal doesn't blank the rest.
/// </summary>
public sealed record BcEnvironmentPanel(
    string EnvironmentName,
    /// <summary>App ids this toolbox has released to this environment, for highlighting our own extensions.</summary>
    IReadOnlySet<Guid> ReleasedAppIds,
    IReadOnlyList<BcInstalledApp> InstalledApps,
    string? InstalledAppsError,
    IReadOnlyList<BcAvailableAppUpdate> AvailableUpdates,
    string? AvailableUpdatesError,
    IReadOnlyList<BcScheduledPteOperation> ScheduledInstalls,
    string? ScheduledInstallsError,
    IReadOnlyList<BcEnvironmentUpdate> EnvironmentUpdates,
    string? EnvironmentUpdatesError,
    /// <summary>When these sections were read from Business Central — a cached panel keeps its original read time, so the page can say how old the answer is.</summary>
    DateTime FetchedAtUtc);
