using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

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
public sealed class ProjectConnectionService
{
    /// <summary>Data Protection purpose string for a project's BC S2S client secret.</summary>
    public const string SecretProtectionPurpose = "ALDevToolbox.ProjectBcSecret";

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly BcTokenService _tokens;
    private readonly IBcAdminClient _adminClient;
    private readonly IBcAutomationClient _automationClient;
    private readonly IDataProtector _secretProtector;
    private readonly ILogger<ProjectConnectionService> _logger;

    public ProjectConnectionService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ProjectAccess access,
        BcTokenService tokens,
        IBcAdminClient adminClient,
        IBcAutomationClient automationClient,
        IDataProtectionProvider protectionProvider,
        ILogger<ProjectConnectionService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _tokens = tokens;
        _adminClient = adminClient;
        _automationClient = automationClient;
        _secretProtector = protectionProvider.CreateProtector(SecretProtectionPurpose);
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; BC connection mutation called outside an authenticated request.");

    /// <summary>Presence/verification view of a project's BC connection — never the secret. Null when the project doesn't exist in this org.</summary>
    public async Task<BcConnectionStatus?> GetConnectionAsync(int projectId, CancellationToken ct = default)
    {
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
        await _access.EnsureCanManageAsync(project.CreatedByUserId, ct);

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
        await _access.EnsureCanManageAsync(project.CreatedByUserId, ct);

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
        catch (BcApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("BC test connection: environments call denied for project {ProjectId} (GDAP).", projectId);
            return new BcConnectionTestResult(BcConnectionResult.GdapMissing, 0,
                "GDAP doesn't appear to be set up for this customer. Grant the delegated admin relationship, then retry.");
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

        _logger.LogInformation("BC test connection succeeded for project {ProjectId}: {Count} environment(s).", projectId, environments.Count);
        return new BcConnectionTestResult(BcConnectionResult.Success, environments.Count,
            environments.Count == 1 ? "Connected. Found 1 environment." : $"Connected. Found {environments.Count} environments.");
    }

    /// <summary>The project's fetched environments (the delivery targets), name-ordered. Read-only.</summary>
    public async Task<IReadOnlyList<ProjectEnvironmentRow>> ListEnvironmentsAsync(int projectId, CancellationToken ct = default)
    {
        return await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.ProjectId == projectId)
            .OrderBy(e => e.Name)
            .Select(e => new ProjectEnvironmentRow(
                e.Id, e.Name, e.Type, e.CompanyId, e.CompanyName, e.FetchedAt, e.MissingSince))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Fetches the companies in one environment from the automation API, for the
    /// pick-company step. Access-gated. Throws <see cref="PlanValidationException"/>
    /// when the environment is unknown and surfaces auth/API failures as a clear message.
    /// </summary>
    public async Task<IReadOnlyList<BcCompany>> FetchCompaniesAsync(int projectId, int environmentId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var project = await _db.OeProjects.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == projectId && c.DeletedAt == null, ct)
            ?? throw Validation("BcTenantId", "This project no longer exists.");
        await _access.EnsureCanManageAsync(project.CreatedByUserId, ct);

        var env = await _db.OeProjectEnvironments.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == environmentId && e.ProjectId == projectId, ct)
            ?? throw Validation("Environment", "That environment no longer exists. Refresh the list and try again.");

        var creds = ResolveCredentials(project)
            ?? throw Validation("BcTenantId", "Enter the connection details first.");

        string token;
        try
        {
            token = await _tokens.GetTokenAsync(projectId, creds.TenantId, creds.ClientId, creds.Secret, ct: ct);
        }
        catch (BcApiException)
        {
            throw Validation("BcClientSecret", "The credentials were rejected. Re-enter them and test the connection again.");
        }

        try
        {
            return await _automationClient.ListCompaniesAsync(token, env.Name, ct);
        }
        catch (BcApiException ex)
        {
            throw Validation("Environment", "Couldn't list companies for this environment. " + ex.Message);
        }
    }

    /// <summary>Records the chosen company for an environment. Access-gated.</summary>
    public async Task PickCompanyAsync(int projectId, int environmentId, Guid companyId, string companyName, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var ownerId = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == projectId)
            .Select(c => c.CreatedByUserId)
            .FirstOrDefaultAsync(ct);
        await _access.EnsureCanManageAsync(ownerId, ct);

        var env = await _db.OeProjectEnvironments
            .FirstOrDefaultAsync(e => e.Id == environmentId && e.ProjectId == projectId, ct)
            ?? throw Validation("Environment", "That environment no longer exists.");

        env.CompanyId = companyId;
        env.CompanyName = (companyName ?? string.Empty).Trim();
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Picked company {Company} for environment {EnvId} (project {ProjectId}).", companyId, environmentId, projectId);
    }

    /// <summary>
    /// Stable upsert of the fetched environments onto the project's tracked
    /// <see cref="Project.Environments"/>: match by name (preserving each row's id and
    /// picked company), add new ones, and stamp <c>MissingSince</c> on any that the
    /// fetch no longer returns rather than deleting them — so a release pipeline's FK
    /// never dangles. Assumes the caller saves.
    /// </summary>
    private async Task UpsertEnvironmentsAsync(Project project, IReadOnlyList<BcEnvironment> fetched, CancellationToken ct)
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
                row.Type = env.Type;
                row.FetchedAt = now;
                row.MissingSince = null; // back if it had vanished
            }
            else
            {
                _db.OeProjectEnvironments.Add(new ProjectEnvironment
                {
                    OrganizationId = project.OrganizationId,
                    ProjectId = project.Id,
                    Name = env.Name,
                    Type = env.Type,
                    FetchedAt = now,
                });
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

    /// <summary>Decrypts the stored credentials, or null when not fully configured / the key ring can't decrypt the secret.</summary>
    private (Guid TenantId, string ClientId, string Secret)? ResolveCredentials(Project project)
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
    Guid? CompanyId,
    string? CompanyName,
    DateTime FetchedAt,
    DateTime? MissingSince);
