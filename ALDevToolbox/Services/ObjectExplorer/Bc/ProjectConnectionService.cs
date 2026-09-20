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

        var orgId = RequireOrganizationId();
        var shared = await _db.OrganizationSettings.AsNoTracking()
            .Where(o => o.OrganizationId == orgId)
            .Select(o => new { o.BcClientId, HasSecret = o.BcClientSecretEncrypted != null, o.BcClientSecretExpiresAt })
            .FirstOrDefaultAsync(ct);
        var sharedAvailable = !string.IsNullOrEmpty(shared?.BcClientId) && shared.HasSecret;

        var usesShared = string.IsNullOrEmpty(p.BcClientId);
        var configured = p.BcTenantId is not null
            && (usesShared ? sharedAvailable : p.HasSecret);
        return new BcConnectionStatus(
            configured, p.BcTenantId, p.BcClientId, p.HasSecret,
            p.BcClientSecretExpiresAt, p.BcCredentialsUpdatedAt, p.BcTimeZone, p.BcConnectionVerifiedAt,
            UsesOrganizationRegistration: usesShared && sharedAvailable,
            OrganizationRegistrationAvailable: sharedAvailable,
            EffectiveSecretExpiresAt: usesShared ? (sharedAvailable ? shared!.BcClientSecretExpiresAt : null) : p.BcClientSecretExpiresAt,
            // Not a secret, and the person wiring up a customer needs it: it is what the
            // customer types into their admin centre to authorise the connection.
            OrganizationClientId: sharedAvailable ? shared!.BcClientId : null);
    }

    // ── The organisation's own app registration ───────────────────────────────

    /// <summary>
    /// The organisation's default app registration, as the Administration page shows it -
    /// never the secret. Admin-only, like the write.
    /// </summary>
    public async Task<OrganizationBcRegistration> GetOrganizationRegistrationAsync(CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        await EnsureOrganizationAdminAsync(ct);
        var row = await _db.OrganizationSettings.AsNoTracking()
            .Where(o => o.OrganizationId == orgId)
            .Select(o => new { o.BcClientId, HasSecret = o.BcClientSecretEncrypted != null, o.BcClientSecretExpiresAt })
            .FirstOrDefaultAsync(ct);
        var (usingIt, withTheirOwn) = await CountSolutionsByRegistrationAsync(ct);
        return new OrganizationBcRegistration(
            row?.BcClientId, row?.HasSecret ?? false, row?.BcClientSecretExpiresAt, usingIt, withTheirOwn);
    }

    /// <summary>
    /// Saves the organisation's default app registration. The secret is keep-on-blank,
    /// as on a solution. Every solution connecting through it re-authenticates on its
    /// next call, and has to be tested again: the last successful test was of the old
    /// credentials.
    /// </summary>
    public async Task SaveOrganizationRegistrationAsync(OrganizationBcRegistrationInput input, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        await EnsureOrganizationAdminAsync(ct);

        var row = await _db.OrganizationSettings.FirstOrDefaultAsync(o => o.OrganizationId == orgId, ct);
        if (row is null)
        {
            row = new OrganizationSettings { OrganizationId = orgId };
            _db.OrganizationSettings.Add(row);
        }

        var errors = new Dictionary<string, string>();
        var clientId = (input.ClientId ?? string.Empty).Trim();
        if (!Guid.TryParse(clientId, out _))
        {
            errors["BcClientId"] = "Enter the app registration's Application (client) ID - a GUID like 00000000-0000-0000-0000-000000000000.";
        }
        var newSecret = input.ClientSecret?.Trim();
        var settingSecret = !string.IsNullOrEmpty(newSecret);
        if (!settingSecret && row.BcClientSecretEncrypted is null)
        {
            errors["BcClientSecret"] = "Enter the app registration's client secret.";
        }
        if (settingSecret && input.SecretExpiresAt is null)
        {
            errors["BcClientSecretExpiresAt"] = "Enter when the secret expires (Entra shows this when you create it).";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        row.BcClientId = clientId.ToLowerInvariant();
        if (settingSecret)
        {
            row.BcClientSecretEncrypted = _secretProtector.Protect(newSecret!);
            row.BcClientSecretExpiresAt = DateTime.SpecifyKind(input.SecretExpiresAt!.Value, DateTimeKind.Utc);
        }
        row.UpdatedAt = DateTime.UtcNow;

        var affected = await ResetSolutionsOnOrganizationRegistrationAsync(ct);
        await _db.SaveChangesAsync(ct);
        foreach (var projectId in affected) _tokens.Invalidate(projectId);

        _logger.LogInformation(
            "User {UserId} saved org {OrgId}'s Business Central app registration (secretChanged={SecretChanged}); {Count} solutions connect through it.",
            _orgContext.CurrentUserId, orgId, settingSecret, affected.Count);
    }

    /// <summary>
    /// Removes the organisation's default app registration. Solutions that were
    /// connecting through it stop connecting until they are given a registration of
    /// their own or a new default is saved - which is why the page says how many first.
    /// </summary>
    public async Task ClearOrganizationRegistrationAsync(CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        await EnsureOrganizationAdminAsync(ct);

        var row = await _db.OrganizationSettings.FirstOrDefaultAsync(o => o.OrganizationId == orgId, ct);
        if (row is null || row.BcClientId is null) return;

        row.BcClientId = null;
        row.BcClientSecretEncrypted = null;
        row.BcClientSecretExpiresAt = null;
        row.UpdatedAt = DateTime.UtcNow;

        var affected = await ResetSolutionsOnOrganizationRegistrationAsync(ct);
        await _db.SaveChangesAsync(ct);
        foreach (var projectId in affected) _tokens.Invalidate(projectId);

        _logger.LogInformation(
            "User {UserId} removed org {OrgId}'s Business Central app registration; {Count} solutions were connecting through it.",
            _orgContext.CurrentUserId, orgId, affected.Count);
    }

    /// <summary>Clears "verified" on every solution with no registration of its own, and returns their ids. The caller saves.</summary>
    private async Task<List<int>> ResetSolutionsOnOrganizationRegistrationAsync(CancellationToken ct)
    {
        var solutions = await _db.OeProjects
            .Where(p => p.DeletedAt == null && p.BcTenantId != null && p.BcClientId == null)
            .ToListAsync(ct);
        foreach (var solution in solutions) solution.BcConnectionVerifiedAt = null;
        return solutions.Select(p => p.Id).ToList();
    }

    private async Task<(int UsingIt, int WithTheirOwn)> CountSolutionsByRegistrationAsync(CancellationToken ct)
    {
        var connected = await _db.OeProjects.AsNoTracking()
            .Where(p => p.DeletedAt == null && p.BcTenantId != null)
            .Select(p => p.BcClientId != null)
            .ToListAsync(ct);
        return (connected.Count(own => !own), connected.Count(own => own));
    }

    private async Task EnsureOrganizationAdminAsync(CancellationToken ct)
    {
        if (!await _access.IsOrganizationAdminAsync(ct))
        {
            throw new ProjectAccessDeniedException("Only an administrator can change your organisation's Business Central app registration.");
        }
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
        var newSecret = input.ClientSecret?.Trim();
        var settingSecret = !input.UseOrganizationRegistration && !string.IsNullOrEmpty(newSecret);
        if (input.UseOrganizationRegistration)
        {
            var orgId = project.OrganizationId;
            var sharedAvailable = await _db.OrganizationSettings.AsNoTracking()
                .AnyAsync(o => o.OrganizationId == orgId && o.BcClientId != null && o.BcClientSecretEncrypted != null, ct);
            if (!sharedAvailable)
            {
                errors["BcClientId"] = "Your organisation has no app registration of its own yet. An administrator can add one under Administration, or enter this customer's here.";
            }
        }
        else
        {
            if (clientId.Length == 0)
            {
                errors["BcClientId"] = "Enter the app registration's client ID.";
            }
            var hasExistingSecret = project.BcClientSecretEncrypted is not null;
            if (!settingSecret && !hasExistingSecret)
            {
                errors["BcClientSecret"] = "Enter the app registration's client secret.";
            }
            if (settingSecret && input.SecretExpiresAt is null)
            {
                errors["BcClientSecretExpiresAt"] = "Enter when the secret expires (Entra shows this when you create it).";
            }
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
        project.BcTimeZone = timeZone;
        if (input.UseOrganizationRegistration)
        {
            // The solution's own registration goes with the choice: a client id left
            // behind is what says "this customer has their own", and a stored secret
            // nobody uses is one more thing to leak.
            project.BcClientId = null;
            project.BcClientSecretEncrypted = null;
            project.BcClientSecretExpiresAt = null;
        }
        else
        {
            project.BcClientId = clientId;
        }
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
        _logger.LogInformation(
            "Saved BC connection for project {ProjectId} (secretChanged={SecretChanged}, organisation's registration={UsesOrganization}).",
            projectId, settingSecret, input.UseOrganizationRegistration);
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
        var creds = await ResolveCredentialsAsync(project, ct);
        if (creds is null)
        {
            return new BcConnectionTestResult(BcConnectionResult.AuthFailed, 0,
                "Enter the connection details (tenant, client ID, and secret) first.");
        }

        string token;
        try
        {
            token = await _tokens.GetTokenAsync(projectId, creds.TenantId, creds.ClientId, creds.Secret, forceRefresh: true, ct);
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

        var creds = await ResolveCredentialsAsync(project, ct)
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

        // A successful read is also the freshest answer to "which modules does this
        // customer have", which people who cannot make this read still need to see.
        if (installed.Error is null && installed.Items.Count > 0)
        {
            try
            {
                await MirrorInstalledAppsAsync(project.OrganizationId, environmentId, installed.Items, ct);
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "Couldn't mirror the installed apps of environment {EnvironmentId}.", environmentId);
            }
        }
        return panel;
    }

    /// <summary>
    /// Brings <c>oe_environment_apps</c> in line with what Business Central just reported,
    /// in place: changed rows are updated, gone ones removed, new ones added. The caller
    /// saves. Never called with an empty list - an environment always has the base
    /// application, so "nothing" is a failed read and must not wipe the last good one.
    /// See <c>.design/solution-customer-info.md</c>, "Modules".
    /// </summary>
    private async Task MirrorInstalledAppsAsync(
        int organizationId, int environmentId, IReadOnlyList<BcInstalledApp> apps, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var existing = await _db.OeEnvironmentApps.Where(a => a.EnvironmentId == environmentId).ToListAsync(ct);
        var byId = existing.ToDictionary(a => a.AppId);
        var reported = new HashSet<Guid>();

        foreach (var app in apps)
        {
            if (!reported.Add(app.AppId)) continue;
            if (!byId.TryGetValue(app.AppId, out var row))
            {
                row = new OeEnvironmentApp { OrganizationId = organizationId, EnvironmentId = environmentId, AppId = app.AppId };
                _db.OeEnvironmentApps.Add(row);
            }
            row.Name = Truncate(app.Name, 250);
            row.Publisher = Truncate(app.Publisher, 250);
            row.Version = Truncate(app.Version, 50);
            row.FetchedAt = now;
        }

        _db.OeEnvironmentApps.RemoveRange(existing.Where(a => !reported.Contains(a.AppId)));
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    /// <summary>
    /// Which of the apps in an environment this toolbox has actually released there.
    /// Best-effort by app id, from the delivery history: enough to tell a consultant
    /// "this pending install is one of yours" instead of leaving them to recognise a
    /// publisher name.
    /// </summary>
    private async Task<IReadOnlySet<Guid>> ReleasedAppIdsAsync(
        int projectId, string environmentName, CancellationToken ct)
    {
        // A delivery snapshots the environment's name at the time it was scheduled, so a
        // soft-deleted environment — which Business Central renames, see the fold in
        // UpsertEnvironmentsAsync — would otherwise lose every release made to it before
        // the deletion. Its pre-deletion name is the same string with the stamp stripped,
        // so match that too.
        var formerName = SoftDeleteStampedBaseName(environmentName);

        var ids = await _db.OeProjectDeliveryResults.AsNoTracking()
            .Where(r => r.AppId != null
                        && r.ProjectDelivery!.ProjectId == projectId
                        && (r.ProjectDelivery.EnvironmentName == environmentName
                            || (formerName != null && r.ProjectDelivery.EnvironmentName == formerName)))
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

        var creds = await ResolveCredentialsAsync(project, ct)
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
    /// What Business Central has done, or is doing, to the environment, newest first -
    /// whoever asked for it. Read live each time: the point of the list is to watch
    /// something finish, which a cache would hide.
    /// </summary>
    public async Task<List<BcEnvironmentOperation>> ListEnvironmentOperationsAsync(
        int projectId, int environmentId, CancellationToken ct = default)
    {
        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct);
        try
        {
            var operations = await _adminClient.ListEnvironmentOperationsAsync(env.Token, env.Family, env.Name, ct);
            return operations.OrderByDescending(o => o.CreatedOn ?? DateTimeOffset.MinValue).ToList();
        }
        catch (BcApiException ex)
        {
            throw Validation("Operations", ex.Message);
        }
    }

    /// <summary>
    /// Who is signed in to the environment right now, longest-running operation first -
    /// which is the one somebody on the phone about a locked posting run is looking for.
    /// <para>
    /// Read live every time and <b>never cached and never stored</b>. A user id and what
    /// that person is doing is personal data with no reason to outlive the screen it is
    /// on, and a cached answer would be wrong in a way a cached operations list is not:
    /// the whole question is who is signed in <em>now</em>. See
    /// <c>.design/environment-updates.md</c>, "Sessions".
    /// </para>
    /// <para>
    /// Manage-gated like every other read that spends the customer's credentials.
    /// </para>
    /// </summary>
    public async Task<List<BcSession>> ListEnvironmentSessionsAsync(
        int projectId, int environmentId, CancellationToken ct = default)
    {
        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct);
        try
        {
            var sessions = await _adminClient.ListSessionsAsync(env.Token, env.Family, env.Name, ct);
            // Longest first, then whoever has been signed in longest: two sessions with
            // nothing running are ordered by the only other thing that distinguishes them.
            return sessions
                .OrderByDescending(s => s.CurrentOperationDuration ?? TimeSpan.Zero)
                .ThenBy(s => s.LogOnDate ?? DateTimeOffset.MaxValue)
                .ThenBy(s => s.SessionId)
                .ToList();
        }
        catch (BcApiException ex)
        {
            throw Validation("Sessions", ex.Message);
        }
    }

    /// <summary>
    /// Ends one session on the customer's environment - the errand behind "posting has
    /// been running for an hour and everything is locked". Gated on managing the solution
    /// and confirmed by name at the page, because somebody loses their unsaved work the
    /// moment it goes through.
    /// <para>
    /// The live list is re-read first, for two reasons. It is the only way the history
    /// line can name whose session it was and what it was running - the id alone answers
    /// nothing a week later, and the session list is never stored - and it turns "that
    /// session has already ended" into a sentence here rather than into a wire 404.
    /// </para>
    /// <para>
    /// Changes the customer's tenant and touches no row of ours, so it is recorded in the
    /// log and in the environment's Toolbox history rather than the audit trail.
    /// </para>
    /// </summary>
    public async Task CancelEnvironmentSessionAsync(
        int projectId, int environmentId, int sessionId, CancellationToken ct = default)
    {
        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct);

        IReadOnlyList<BcSession> live;
        try
        {
            live = await _adminClient.ListSessionsAsync(env.Token, env.Family, env.Name, ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("Sessions", ex.Message);
        }

        var session = live.FirstOrDefault(s => s.SessionId == sessionId)
            ?? throw Validation("Sessions",
                $"That session is no longer signed in to {env.Name}. Refresh the list to see who is.");

        try
        {
            await _adminClient.CancelSessionAsync(env.Token, env.Family, env.Name, sessionId, ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("Sessions", ex.Message);
        }

        await RecordEnvironmentActionAsync(projectId, env.Id, UpgradeActionKind.CancelSession,
            BcSessionDisplay.HistoryLine(session), ct);

        // The user id is in the history line, which is the record this action leaves; the
        // log keeps the ids that identify the call, not a second copy of the person.
        _logger.LogInformation(
            "User {UserId} ended session {SessionId} ({ClientType}) on {Environment} (project {ProjectId}).",
            _orgContext.CurrentUserId, sessionId, session.ClientType, env.Name, projectId);
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
    /// Brings back an environment the customer deleted, while Business Central is still
    /// keeping it — the one write here that undoes somebody else's decision, so it is
    /// gated on managing the solution and confirmed by name at the page.
    /// <para>
    /// Refuses an environment that isn't deleted: there is nothing to recover, and
    /// Business Central would answer with a code rather than a sentence. Afterwards the
    /// customer's environments are re-read, because Microsoft schedules the recovery
    /// rather than doing it there and then and the row's state has to move on its own.
    /// A failed re-read costs the freshness, never the write.
    /// </para>
    /// <para>
    /// Changes the customer's tenant and touches no row of ours, so it is recorded in the
    /// log and in the environment's Toolbox history rather than the audit trail — see
    /// <c>.design/environment-updates.md</c>, "Deleted environments".
    /// </para>
    /// </summary>
    public async Task RecoverEnvironmentAsync(int projectId, int environmentId, CancellationToken ct = default)
    {
        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct);

        var row = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == env.Id)
            .Select(e => new { e.SoftDeletedOn, e.Status })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("Environment", "That environment no longer exists. Refresh the list and try again.");

        if (row.SoftDeletedOn is null && !BcEnvironmentStatus.IsSoftDeleted(row.Status))
        {
            throw Validation("Environment",
                $"{env.Name} hasn't been deleted, so there is nothing to bring back.");
        }

        try
        {
            await _adminClient.RecoverEnvironmentAsync(env.Token, env.Family, env.Name, ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("Environment", ex.Message);
        }

        await RecordEnvironmentActionAsync(projectId, env.Id, UpgradeActionKind.RecoverEnvironment,
            $"Asked Business Central to bring {env.Name} back.", ct);

        _panelCache.Invalidate(projectId, environmentId);

        _logger.LogInformation(
            "User {UserId} asked for the deleted environment {Environment} (project {ProjectId}) to be recovered.",
            _orgContext.CurrentUserId, env.Name, projectId);

        try
        {
            // Already gated above, which is what this entry point requires of its callers.
            await RefreshEnvironmentsUnattendedAsync(projectId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Recovered {Environment} but couldn't re-read project {ProjectId}'s environments afterwards.",
                env.Name, projectId);
        }
    }

    /// <summary>
    /// Copies an environment into a new one — almost always a customer's production into a
    /// fresh sandbox, to try an update or reproduce a problem on real data. Gated on
    /// managing the solution and confirmed by name at the page, because the copy adds an
    /// environment to the customer's tenant: it counts against their storage allowance,
    /// and a production copy against their licences.
    /// <para>
    /// Refuses before anything is sent when the source is one the customer has deleted, or
    /// one Business Central is not reporting as ready — a copy of an environment part-way
    /// through an update would be a copy of an unknown moment. The name is checked against
    /// Business Central's rules and against the names this solution already has, so the
    /// common mistake is answered here rather than as a wire code.
    /// </para>
    /// <para>
    /// Microsoft schedules the copy rather than making it there and then, so the customer's
    /// environments are re-read afterwards; the new one appears as <c>Preparing</c> once
    /// Business Central lists it, which may not be on this read. A failed re-read costs the
    /// freshness, never the write. Recorded in the log and in the <em>source</em>
    /// environment's Toolbox history — see <c>.design/environment-updates.md</c>,
    /// "Copying an environment".
    /// </para>
    /// </summary>
    /// <returns>The operation Business Central scheduled, for the log and the Operations tab.</returns>
    public async Task<BcEnvironmentCopy> CopyEnvironmentAsync(
        int projectId, int sourceEnvironmentId, string newName, string targetType, CancellationToken ct = default)
    {
        if (BcEnvironmentName.Validate(newName) is { } nameProblem)
        {
            throw Validation("NewName", nameProblem);
        }
        if (BcEnvironmentTypes.Normalize(targetType) is not { } type)
        {
            throw Validation("TargetType", "Choose whether the copy is a sandbox or a production environment.");
        }
        var name = newName.Trim();

        var env = await ResolveEnvironmentAsync(projectId, sourceEnvironmentId, ct);

        var source = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == env.Id)
            .Select(e => new { e.SoftDeletedOn, e.Status, e.Type })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("Environment", "That environment no longer exists. Refresh the list and try again.");

        if (source.SoftDeletedOn is not null || BcEnvironmentStatus.IsSoftDeleted(source.Status))
        {
            throw Validation("Environment",
                $"{env.Name} has been deleted, so there is nothing to copy. Bring it back first.");
        }
        // The same reading the delivery gate makes: ready, or a status we have no opinion
        // about. Anything else is a moving target, and a copy of one is a copy of nothing
        // anybody can name.
        if (!BcEnvironmentStatus.CanPublish(source.Status))
        {
            throw Validation("Environment",
                $"Business Central reports {env.Name} as {BcEnvironmentStatus.Humanise(source.Status).ToLowerInvariant()} right now, "
                + "so it can't be copied. Wait until it is running again, then try again.");
        }

        // Our mirror, not Business Central's word - but it catches the common mistake
        // before a round trip, and says which environment is in the way.
        var taken = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.MissingSince == null)
            .Select(e => e.Name)
            .ToListAsync(ct);
        if (taken.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw Validation("NewName", $"This solution already has an environment called {name}. Pick another name.");
        }

        BcEnvironmentCopy copy;
        try
        {
            copy = await _adminClient.CopyEnvironmentAsync(env.Token, env.Family, env.Name, name, type, ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("Environment", ex.Message);
        }

        await RecordEnvironmentActionAsync(projectId, env.Id, UpgradeActionKind.CopyEnvironment,
            $"Copied to {name}, a {type.ToLowerInvariant()} environment.", ct);

        _logger.LogInformation(
            "User {UserId} asked Business Central to copy {Environment} ({SourceType}) to {NewEnvironment} ({Type}) in project {ProjectId}; operation {OperationId} is {Status}.",
            _orgContext.CurrentUserId, env.Name, source.Type, name, type, projectId, copy.OperationId, copy.Status);

        try
        {
            // Already gated above, which is what this entry point requires of its callers.
            // The copy takes a while, so the new environment may not be listed yet - that
            // is not a failure, and the page says where to watch it instead.
            await RefreshEnvironmentsUnattendedAsync(projectId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Asked for a copy of {Environment} but couldn't re-read project {ProjectId}'s environments afterwards.",
                env.Name, projectId);
        }

        return copy;
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

        if (BcUpdateSchedule.EffectiveLatest(next.LatestSelectableDateTime) is not { } latest)
        {
            throw Validation("Update", "Business Central hasn't given this update a last possible date, so it can't be moved.");
        }
        // On or after, by calendar day in UTC rather than by tick: Business Central stores
        // the date at the start of the environment's own update window, so an update that
        // is already as late as it can go reads back a different time of day from the one
        // we would send — and a window starting after midnight UTC (02:00 in Copenhagen is
        // 01:00 UTC) lands it on the following day. Either way there is nowhere left to
        // move it to, and re-sending would only fail against the bound.
        if (next.SelectedDateTime?.UtcDateTime.Date >= latest.UtcDateTime.Date)
        {
            throw Validation("Update", "This update's date is already the latest Microsoft allows.");
        }

        await WriteUpdateScheduleAsync(env, next, latest, ignoreUpdateWindow: null, ct, verifyDateMoved: true);
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
    /// <para>
    /// With <paramref name="verifyDateMoved"/> the re-read is also the proof that the
    /// write landed. Issue #804 saw the move recorded as done while the date stayed exactly
    /// where it was, so the write is verified rather than trusted, and a history entry
    /// saying "done" for a date that never moved is worse than no entry at all. The test is
    /// whether the date <em>changed</em>, not whether it landed where we asked: Business
    /// Central puts it at the start of the customer's update window, which can be the
    /// following UTC day. A re-read that <em>fails</em> still only costs the freshness — it
    /// is a re-read that succeeds with an unchanged date that fails the action.
    /// </para>
    /// </summary>
    private async Task WriteUpdateScheduleAsync(
        (string Token, string Family, string Name, int Id) env,
        BcEnvironmentUpdate update,
        DateTimeOffset selectedDateTime,
        bool? ignoreUpdateWindow,
        CancellationToken ct,
        bool verifyDateMoved = false)
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
        BcEnvironmentUpdate? stored;
        try
        {
            var updates = await _adminClient.ListEnvironmentUpdatesAsync(env.Token, env.Family, env.Name, ct);
            stored = PickNextUpdate(updates);
            var row = await _db.OeProjectEnvironments.FirstOrDefaultAsync(e => e.Id == env.Id, ct);
            if (row is not null)
            {
                ApplyNextUpdate(row, stored);
                await _db.SaveChangesAsync(ct);
            }
        }
        catch (BcApiException ex)
        {
            _logger.LogWarning(
                "The update date on {Environment} was changed, but re-reading it failed: {Message}. The cached row stays stale until the next refresh.",
                env.Name, ex.Message);
            return;
        }

        if (!verifyDateMoved) return;

        // Did it move, not did it land where we asked: the stored date is the start of the
        // customer's update window, so a window opening after midnight UTC legitimately
        // puts it on the day after the one we sent. Two nulls count as unchanged.
        if (stored?.SelectedDateTime != update.SelectedDateTime) return;

        var landed = stored?.SelectedDateTime?.UtcDateTime;
        _logger.LogWarning(
            "Business Central kept {Environment} on {StoredDateTime} after being asked for {SelectedDateTime}, so the move was not recorded as done.",
            env.Name, landed, selectedDateTime);

        throw Validation("Update", landed is { } value
            ? $"Business Central did not accept the new date. Its schedule still says {value:yyyy-MM-dd}."
            : "Business Central did not accept the new date. Its schedule still has no date.");
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

        var creds = await ResolveCredentialsAsync(project, ct)
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
    /// Updates one AppSource app on the environment to the version Business Central has
    /// waiting for it. Manage-gated, like the other writes that are not about the platform
    /// update.
    /// <para>
    /// The version is not taken on trust. The waiting updates are read again first, and
    /// the write goes ahead only for an app that is on that list, at exactly that version
    /// - so a stale page, or a caller that is not the page, cannot move an app to a
    /// version Business Central never offered.
    /// </para>
    /// <para>
    /// An app that waits for others is updated together with them, but only the ones in
    /// <paramref name="confirmedPrerequisiteAppIds"/>: the apps somebody was shown and
    /// agreed to. If Business Central now asks for one that is not in that set the write
    /// is refused, so nobody's agreement covers an app they never saw.
    /// </para>
    /// <para>
    /// Changes the customer's tenant and touches no row of ours, so like Microsoft 365
    /// access it is recorded in the log rather than the audit trail - see
    /// <c>.design/saas-delivery.md</c>.
    /// </para>
    /// </summary>
    /// <param name="useUpdateWindow">True to let it run in the environment's next update window; false starts it now.</param>
    /// <param name="confirmedPrerequisiteAppIds">The apps the caller agreed may be installed or updated alongside; null or empty for none.</param>
    /// <returns>The operation Business Central started or scheduled.</returns>
    public async Task<BcAppOperation> UpdateAppAsync(
        int projectId, int environmentId, Guid appId, string targetVersion, bool useUpdateWindow,
        IReadOnlyCollection<Guid>? confirmedPrerequisiteAppIds = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            throw Validation("App", "Choose the version to update to.");
        }

        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct);
        var confirmed = confirmedPrerequisiteAppIds ?? Array.Empty<Guid>();

        BcAppOperation operation;
        try
        {
            var waiting = await _apps.ListAvailableUpdatesAsync(env.Token, env.Family, env.Name, ct);
            var offered = waiting.FirstOrDefault(u => u.AppId == appId)
                ?? throw Validation("App", "Business Central no longer has an update waiting for that app. Refresh and look again.");
            if (!string.Equals(offered.Version, targetVersion.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw Validation("App", $"Business Central now offers {offered.Name} {offered.Version}, not {targetVersion}. Refresh and try again.");
            }
            var unconfirmed = offered.Requirements
                .Where(r => r.AppId is not { } id || !confirmed.Contains(id))
                .ToList();
            if (unconfirmed.Count > 0)
            {
                throw Validation("App", confirmed.Count == 0
                    ? $"{offered.Name} has to wait for {string.Join(", ", unconfirmed.Select(r => r.Name))} to be updated first."
                    : $"{offered.Name} now also waits for {string.Join(", ", unconfirmed.Select(r => r.Name))}. Refresh and look again.");
            }

            operation = await _apps.UpdateAppAsync(env.Token, env.Family, env.Name, appId, offered.Version, useUpdateWindow,
                installOrUpdateNeededDependencies: offered.Requirements.Count > 0, ct);

            var alongside = offered.Requirements.Count == 0
                ? string.Empty
                : $" Along with {string.Join(", ", offered.Requirements.Select(r => r.Name))}.";
            await RecordEnvironmentActionAsync(projectId, env.Id, UpgradeActionKind.UpdateApp,
                $"{offered.Name} to {offered.Version}, {(useUpdateWindow ? "in the BC update window" : "right away")}.{alongside}", ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("App", "Business Central didn't start the update. " + ex.Message);
        }

        _panelCache.Invalidate(projectId, environmentId);

        _logger.LogInformation(
            "User {UserId} asked for app {AppId} to be updated to {Version} on {Environment} (project {ProjectId}, in the update window: {InWindow}, along with {PrerequisiteCount} prerequisites); operation {OperationId}.",
            _orgContext.CurrentUserId, appId, targetVersion, env.Name, projectId, useUpdateWindow, confirmed.Count, operation.Id);
        return operation;
    }

    /// <summary>
    /// Installs one extension package somebody uploaded by hand - an app another company
    /// built, which has no pipeline here to release it from. Manage-gated.
    /// <para>
    /// Only the two schedules Business Central allows for an app it has not seen before
    /// are accepted (right away, or in the update window), and the sync mode is always
    /// Add: Force sync can drop the customer's columns, and that is not a choice to make
    /// about a package we did not build. Dependencies are not pulled along either - a
    /// missing one is refused by name, so nothing is installed that nobody picked.
    /// </para>
    /// <para>
    /// The package is passed straight through and never stored, and the write touches no
    /// row of ours, so like an app update it is recorded in the log rather than the audit
    /// trail - see <c>.design/saas-delivery.md</c>.
    /// </para>
    /// </summary>
    /// <returns>The operation Business Central started or scheduled.</returns>
    public async Task<BcAppOperation> InstallUploadedAppAsync(
        int projectId, int environmentId, byte[] appBytes, string fileName, bool useUpdateWindow, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(appBytes);
        // Either separator, whatever this host runs on: a Windows path is not one to Linux.
        var name = (fileName ?? string.Empty).Trim();
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        if (!name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            throw Validation("App", "Choose an extension package - a file ending in .app.");
        }
        if (appBytes.Length == 0)
        {
            throw Validation("App", $"{name} is empty.");
        }
        if (appBytes.Length > BcAppManagementClient.MaxAppBytes)
        {
            throw Validation("App", $"{name} is over 50 MB, which is the largest app Business Central accepts.");
        }

        var env = await ResolveEnvironmentAsync(projectId, environmentId, ct);

        BcAppOperation operation;
        try
        {
            operation = await _apps.InstallPteAsync(
                env.Token, env.Family, env.Name, appBytes, name,
                useUpdateWindow ? BcDeploymentSchedule.UpdateWindow : BcDeploymentSchedule.Immediate,
                BcSyncMode.Add,
                // No language, for the reason a delivery sends none: see DeliveryService.
                languageId: string.Empty,
                installOrUpdateNeededDependencies: false,
                ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("App", "Business Central didn't accept the app. " + ex.Message);
        }

        await RecordEnvironmentActionAsync(projectId, env.Id, UpgradeActionKind.UploadApp,
            $"{name}{(string.IsNullOrWhiteSpace(operation.TargetAppVersion) ? "" : $" (version {operation.TargetAppVersion})")}, {(useUpdateWindow ? "in the BC update window" : "right away")}.", ct);

        _panelCache.Invalidate(projectId, environmentId);

        _logger.LogInformation(
            "User {UserId} uploaded {FileName} ({Bytes} bytes) to {Environment} (project {ProjectId}, in the update window: {InWindow}); app {AppId} version {Version}, operation {OperationId}.",
            _orgContext.CurrentUserId, name, appBytes.Length, env.Name, projectId, useUpdateWindow,
            operation.AppId, operation.TargetAppVersion, operation.Id);
        return operation;
    }

    /// <summary>
    /// Puts a line in the environment's update history for something that was sent to
    /// the customer's tenant there and then. Written already <c>Sent</c>, so the worker
    /// that fires booked actions never sees it. The write to Business Central has
    /// happened by now and cannot be taken back, so a failure to record it is logged and
    /// swallowed: reporting the update as failed would be the bigger lie.
    /// </summary>
    private async Task RecordEnvironmentActionAsync(
        int projectId, int environmentId, UpgradeActionKind kind, string outcome, CancellationToken ct)
    {
        try
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            _db.OeEnvironmentUpgradeActions.Add(new OeEnvironmentUpgradeAction
            {
                OrganizationId = RequireOrganizationId(),
                ProjectId = projectId,
                EnvironmentId = environmentId,
                Kind = kind,
                Status = UpgradeActionStatus.Sent,
                RequestedByUserId = _orgContext.CurrentUserId,
                RequestedBy = await AuditActor.ResolveAsync(_db, _orgContext.CurrentUserId, ct),
                RequestedAt = now,
                ExecuteAfter = now,
                SentAt = now,
                Outcome = outcome,
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Couldn't record {Kind} in the update history of environment {EnvironmentId}.", kind, environmentId);
        }
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

        var creds = await ResolveCredentialsAsync(project, ct)
            ?? throw new BcApiException(null,
                "The Business Central connection isn't set up (or its secret can't be decrypted). Re-enter it on the project's Business Central page.");

        if (creds.ExpiresAt is { } expiry && expiry <= DateTime.UtcNow)
        {
            // Never a quiet switch to the other registration: which one a customer has
            // authorised is theirs to know, and a fallback would hide it.
            throw new BcApiException(null, creds.FromOrganization
                ? "Your organisation's Business Central client secret has expired. An administrator has to rotate it in Entra and re-enter it under Administration before releasing."
                : "This solution's own Business Central client secret has expired. Rotate it in Entra and re-enter it on the solution's Business Central tab, or switch the solution to your organisation's app registration there.");
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
    /// <para>
    /// One wrinkle makes "match by name" not quite enough: when a customer soft-deletes
    /// an environment, Business Central hands the name back under a <em>new</em> one with
    /// the deletion time appended (<c>JLE</c> returns as <c>JLE-260911110359</c>), so the
    /// original is free to be reused. Read literally that is one environment vanishing
    /// and a stranger appearing, which is what issue #808 saw on screen. The fold below
    /// puts the pair back onto the row the pipelines already point at. See
    /// <c>.design/saas-delivery.md</c> ("soft_deleted_on and missing_since").
    /// </para>
    /// </summary>
    private async Task UpsertEnvironmentsAsync(OeProject project, IReadOnlyList<BcEnvironment> fetched, CancellationToken ct)
    {
        var existing = await _db.OeProjectEnvironments
            .Where(e => e.ProjectId == project.Id)
            .ToListAsync(ct);
        var byName = existing.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fetchedNames = fetched.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var env in fetched)
        {
            seen.Add(env.Name);
            if (byName.TryGetValue(env.Name, out var row))
            {
                ApplyFetched(row, env, now);
                row.MissingSince = null; // back if it had vanished

                // The fold below only happens the first time the renamed environment is
                // seen. A solution that met it before the fold existed has both rows
                // already - the old name, "no longer present" for good, and the stamped
                // one - so a refresh that finds the pair puts them back together.
                if (FoldTarget(env, byName, fetchedNames) is { } stale && stale.Id != row.Id)
                {
                    await AbsorbStaleTwinAsync(row, stale, ct);
                    byName.Remove(stale.Name);
                    existing.Remove(stale);
                    _logger.LogInformation(
                        "Merged environment row {StaleEnvironmentName} into its soft-deleted continuation {EnvironmentName} for project {ProjectId}.",
                        stale.Name, row.Name, project.Id);
                }
                continue;
            }

            if (FoldTarget(env, byName, fetchedNames) is { } renamed)
            {
                _logger.LogInformation(
                    "Business Central renamed soft-deleted environment {OldEnvironmentName} to {NewEnvironmentName} for project {ProjectId}; folded onto the existing row.",
                    renamed.Name, env.Name, project.Id);
                seen.Add(renamed.Name); // so the pass below doesn't call the old name missing
                renamed.Name = env.Name; // the API name is what later admin-center calls address
                byName[env.Name] = renamed;
                ApplyFetched(renamed, env, now);
                renamed.MissingSince = null;
                continue;
            }

            // Nothing to fold onto — including the reverse race, where the first refresh
            // after the deletion is also the first time we hear of the environment at
            // all. Then the suffixed name is simply a new environment, soft-deleted from
            // the moment we meet it, and that is the honest thing to show.
            var row2 = new OeProjectEnvironment
            {
                OrganizationId = project.OrganizationId,
                ProjectId = project.Id,
                Name = env.Name,
            };
            ApplyFetched(row2, env, now);
            _db.OeProjectEnvironments.Add(row2);
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
    /// Merges the row an environment had under its old name into the row it has under its
    /// soft-deleted one. The stamped row survives, because its name is the one the API
    /// answers to and keeping it means nothing is renamed into a unique index mid-save.
    /// What the old row carried comes across first: its release pipelines (which would
    /// otherwise block the delete), its update history, and the delivery window somebody
    /// set on it if the survivor has none. The caller saves.
    /// </summary>
    private async Task AbsorbStaleTwinAsync(OeProjectEnvironment survivor, OeProjectEnvironment stale, CancellationToken ct)
    {
        var pipelines = await _db.OeReleasePipelines
            .Where(r => r.ProjectEnvironmentId == stale.Id)
            .ToListAsync(ct);
        foreach (var pipeline in pipelines) pipeline.ProjectEnvironmentId = survivor.Id;

        var actions = await _db.OeEnvironmentUpgradeActions
            .Where(a => a.EnvironmentId == stale.Id)
            .ToListAsync(ct);
        foreach (var action in actions) action.EnvironmentId = survivor.Id;

        if (survivor.UpdateWindowStart is null && survivor.UpdateWindowEnd is null)
        {
            survivor.UpdateWindowStart = stale.UpdateWindowStart;
            survivor.UpdateWindowEnd = stale.UpdateWindowEnd;
        }

        _db.OeProjectEnvironments.Remove(stale);
    }

    /// <summary>
    /// The existing row a freshly-seen, soft-deleted environment is the renamed
    /// continuation of, or null when there is none and it should be inserted as new.
    /// Deliberately narrow: only a soft-deleted fetch with a deletion stamp on its name
    /// folds, only onto a row that isn't itself soft-deleted, and only when the base name
    /// is absent from this same fetch — a customer who has already created a fresh
    /// <c>JLE</c> alongside the deleted one must keep two rows.
    /// </summary>
    private static OeProjectEnvironment? FoldTarget(
        BcEnvironment env,
        Dictionary<string, OeProjectEnvironment> byName,
        HashSet<string> fetchedNames)
    {
        if (!IsSoftDeleted(env)) return null;
        if (SoftDeleteStampedBaseName(env.Name) is not { } baseName) return null;
        if (fetchedNames.Contains(baseName)) return null;
        if (!byName.TryGetValue(baseName, out var row)) return null;
        return row.SoftDeletedOn is null ? row : null;
    }

    /// <summary>
    /// True when the API says this environment has been soft-deleted. Either signal is
    /// enough: the stamp can arrive without the status and the other way round.
    /// </summary>
    private static bool IsSoftDeleted(BcEnvironment env) =>
        env.SoftDeletedOn is not null || BcEnvironmentStatus.IsSoftDeleted(env.Status);

    /// <summary>
    /// Strips the <c>-yyMMddHHmmss</c> deletion stamp Business Central appends when an
    /// environment is soft-deleted, returning the name it had before
    /// (<c>JLE-260911110359</c> → <c>JLE</c>), or null when the name carries no such
    /// stamp. Twelve digits are all this checks: whether they parse as a plausible date
    /// is Microsoft's business, and the caller only folds a name that the API has also
    /// told us is soft-deleted.
    /// </summary>
    internal static string? SoftDeleteStampedBaseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var dash = name.LastIndexOf('-');
        if (dash <= 0 || name.Length - dash - 1 != 12) return null;
        for (var i = dash + 1; i < name.Length; i++)
        {
            if (!char.IsAsciiDigit(name[i])) return null;
        }
        return name[..dash];
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

        // Storage is the tenant's: one read covers every environment's size and the one
        // allowance they share. A failure costs the freshness, never the figures.
        try
        {
            var storage = await _adminClient.GetTenantStorageAsync(token, ct);
            foreach (var row in rows)
            {
                row.BcDatabaseKb = storage.DatabaseKilobytesByEnvironment.TryGetValue(row.Name, out var kb) ? kb : null;
            }
            project.BcStorageQuotaKb = storage.AllowedKilobytes;
            project.BcStorageFetchedAt = _clock.GetUtcNow().UtcDateTime;
        }
        catch (BcApiException ex)
        {
            _logger.LogWarning(
                "Couldn't read the storage figures for project {ProjectId}: {Message}.", project.Id, ex.Message);
        }

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

            ct.ThrowIfCancellationRequested();
            try
            {
                var family = string.IsNullOrWhiteSpace(row.ApplicationFamily) ? BcConstants.DefaultApplicationFamily : row.ApplicationFamily;
                var apps = await _apps.ListInstalledAppsAsync(token, family, row.Name, ct);
                if (apps.Count > 0) await MirrorInstalledAppsAsync(project.OrganizationId, row.Id, apps, ct);
            }
            catch (BcApiException ex)
            {
                _logger.LogWarning(
                    "Couldn't read the installed apps for {Environment} (project {ProjectId}): {Message}.",
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
    /// <summary>The credentials a solution connects with, and where they came from. Never leaves this service.</summary>
    private sealed record ResolvedCredentials(Guid TenantId, string ClientId, string Secret, DateTime? ExpiresAt, bool FromOrganization);

    /// <summary>
    /// A solution connects with its own app registration when it has one, and otherwise
    /// with the organisation's. The choice is the solution's client id: set means "this
    /// customer has a registration of their own", and then the organisation's is never
    /// tried - not even when the solution's own secret is missing or has expired. A
    /// fallback there would connect a customer through a registration nobody chose for
    /// them, so it fails and says so instead.
    /// </summary>
    private async Task<ResolvedCredentials?> ResolveCredentialsAsync(OeProject project, CancellationToken ct)
    {
        if (project.BcTenantId is null || project.BcTenantId == Guid.Empty) return null;

        string clientId;
        string? encrypted;
        DateTime? expiresAt;
        var fromOrganization = string.IsNullOrEmpty(project.BcClientId);
        if (fromOrganization)
        {
            // Scoped by the query filter as well; the predicate names the solution's own
            // organisation so the read is pinned whichever context it runs under.
            var shared = await _db.OrganizationSettings.AsNoTracking()
                .Where(o => o.OrganizationId == project.OrganizationId)
                .Select(o => new { o.BcClientId, o.BcClientSecretEncrypted, o.BcClientSecretExpiresAt })
                .FirstOrDefaultAsync(ct);
            if (string.IsNullOrEmpty(shared?.BcClientId)) return null;
            (clientId, encrypted, expiresAt) = (shared.BcClientId, shared.BcClientSecretEncrypted, shared.BcClientSecretExpiresAt);
        }
        else
        {
            (clientId, encrypted, expiresAt) = (project.BcClientId!, project.BcClientSecretEncrypted, project.BcClientSecretExpiresAt);
        }
        if (string.IsNullOrEmpty(encrypted)) return null;

        try
        {
            return new ResolvedCredentials(
                project.BcTenantId.Value, clientId, _secretProtector.Unprotect(encrypted), expiresAt, fromOrganization);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            _logger.LogError(ex,
                "Could not decrypt the BC client secret for project {ProjectId} (organisation's registration: {FromOrganization}); it must be re-entered.",
                project.Id, fromOrganization);
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

/// <summary>The organisation's default app registration as the Administration page shows it. Never carries the secret.</summary>
public sealed record OrganizationBcRegistration(
    string? ClientId,
    bool HasSecret,
    DateTime? SecretExpiresAt,
    int SolutionsUsingIt,
    int SolutionsWithTheirOwn)
{
    public bool IsConfigured => !string.IsNullOrEmpty(ClientId) && HasSecret;
}

/// <summary>Form-post shape for the organisation's app registration. The secret is keep-on-blank.</summary>
public sealed record OrganizationBcRegistrationInput(string? ClientId, string? ClientSecret, DateTime? SecretExpiresAt);

/// <summary>Form-post shape for a project's BC connection. The secret is keep-on-blank (empty leaves the stored one).</summary>
public sealed record BcConnectionInput(
    Guid? TenantId,
    string? ClientId,
    string? ClientSecret,
    DateTime? SecretExpiresAt,
    string? TimeZone,
    /// <summary>True to connect with the organisation's app registration; the solution's own client id, secret and expiry are then cleared.</summary>
    bool UseOrganizationRegistration = false);

/// <summary>Presence/verification view of a project's BC connection. Never carries the secret.</summary>
public sealed record BcConnectionStatus(
    bool IsConfigured,
    Guid? TenantId,
    string? ClientId,
    bool HasSecret,
    DateTime? SecretExpiresAt,
    DateTime? CredentialsUpdatedAt,
    string? TimeZone,
    DateTime? VerifiedAt,
    /// <summary>True when the solution has no registration of its own and the organisation's is what it connects with.</summary>
    bool UsesOrganizationRegistration = false,
    /// <summary>True when the organisation has a complete app registration a solution could use.</summary>
    bool OrganizationRegistrationAvailable = false,
    /// <summary>When the secret actually in use expires - the solution's own, or the organisation's.</summary>
    DateTime? EffectiveSecretExpiresAt = null,
    /// <summary>The organisation's client id, when it has a complete registration - what a customer authorises in their admin centre.</summary>
    string? OrganizationClientId = null);

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
