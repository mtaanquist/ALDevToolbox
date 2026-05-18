using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Read-side view of <see cref="SystemSettings"/> for the SiteAdmin form.
/// Carries <see cref="HasSmtpPassword"/> rather than the password itself —
/// plaintext is only ever materialised inside <see cref="ResolvedSmtpSettings"/>
/// for the email sender.
/// </summary>
public sealed record SystemSettingsView(
    string? SmtpHost,
    int? SmtpPort,
    string? SmtpUser,
    bool HasSmtpPassword,
    string? SmtpFrom,
    bool? SmtpUseStartTls,
    string? BannerText,
    bool DefaultSignupAutoApprove,
    bool BackupScheduleEnabled,
    TimeOnly BackupScheduleTimeUtc,
    int BackupRetentionCount,
    int PerTenantBackupRetentionCount,
    int? DefaultStorageQuotaMb,
    decimal IndexSizeMultiplier,
    bool McpEnabled,
    DateTime UpdatedAt);

/// <summary>
/// Input for <see cref="SystemSettingsService.SaveAsync"/>.
/// <see cref="SmtpPassword"/> replaces the stored password when non-empty;
/// a blank value leaves it untouched (the form posts blank when the
/// SiteAdmin doesn't re-type the password). To clear an existing password,
/// set <see cref="ClearSmtpPassword"/> instead.
/// </summary>
public sealed record SystemSettingsInput(
    string? SmtpHost,
    int? SmtpPort,
    string? SmtpUser,
    string? SmtpPassword,
    bool ClearSmtpPassword,
    string? SmtpFrom,
    bool? SmtpUseStartTls,
    string? BannerText,
    bool DefaultSignupAutoApprove,
    bool BackupScheduleEnabled,
    TimeOnly BackupScheduleTimeUtc,
    int BackupRetentionCount,
    int PerTenantBackupRetentionCount,
    int? DefaultStorageQuotaMb,
    decimal IndexSizeMultiplier,
    bool McpEnabled);

/// <summary>
/// SiteAdmin-facing view of the off-site backup settings. Carries flags
/// for whether keys are stored rather than the keys themselves; plaintext
/// only ever materialises in <see cref="ResolvedOffsiteSettings"/>.
/// </summary>
public sealed record OffsiteSettingsView(
    bool Enabled,
    string? Endpoint,
    string? Region,
    string? Bucket,
    string? Prefix,
    bool HasAccessKey,
    bool HasSecretKey,
    bool ForcePathStyle,
    int RetentionDays);

/// <summary>
/// Input for <see cref="SystemSettingsService.SaveOffsiteAsync"/>. Empty
/// access/secret values leave the stored value untouched (same pattern as
/// SMTP password); set the explicit "Clear" flags to wipe them.
/// </summary>
public sealed record OffsiteSettingsInput(
    bool Enabled,
    string? Endpoint,
    string? Region,
    string? Bucket,
    string? Prefix,
    string? AccessKey,
    bool ClearAccessKey,
    string? SecretKey,
    bool ClearSecretKey,
    bool ForcePathStyle,
    int RetentionDays);

/// <summary>
/// Fully resolved off-site configuration with plaintext credentials.
/// Held only inside <see cref="OffsiteBackupService"/>; never persisted,
/// never logged.
/// </summary>
public sealed record ResolvedOffsiteSettings(
    string? Endpoint,
    string? Region,
    string Bucket,
    string? Prefix,
    string AccessKey,
    string SecretKey,
    bool ForcePathStyle,
    int RetentionDays);

/// <summary>
/// Resolved SMTP configuration. Either fully populated (host + from set) or
/// considered unconfigured. The plaintext password is only ever held in this
/// record — never persisted, never logged.
/// </summary>
public sealed record ResolvedSmtpSettings(
    string Host,
    int Port,
    string? User,
    string? Password,
    string From,
    bool UseStartTls)
{
    public static ResolvedSmtpSettings? TryFrom(string? host, int? port, string? user, string? password, string? from, bool? useStartTls)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(from)) return null;
        return new ResolvedSmtpSettings(
            Host: host!,
            Port: port ?? 587,
            User: string.IsNullOrEmpty(user) ? null : user,
            Password: string.IsNullOrEmpty(password) ? null : password,
            From: from!,
            UseStartTls: useStartTls ?? true);
    }
}

/// <summary>
/// Reads and writes the singleton <see cref="SystemSettings"/> row. The
/// SMTP password is encrypted via ASP.NET Core Data Protection; decryption
/// is contained here, so callers see either a plaintext-bearing
/// <see cref="ResolvedSmtpSettings"/> (for the email sender) or a
/// <see cref="SystemSettingsView"/> (for the SiteAdmin form).
///
/// <para>
/// <see cref="ResolveSmtpAsync"/> prefers DB values and falls back to
/// <c>SMTP_*</c> env vars — fresh deployments can fire signup-approval
/// emails before any SiteAdmin has logged in to fill the form.
/// </para>
/// </summary>
public sealed class SystemSettingsService
{
    /// <summary>Data Protection purpose string for SMTP passwords.</summary>
    public const string SmtpPasswordProtectionPurpose = "ALDevToolbox.SystemSettings.SmtpPassword";

    /// <summary>Data Protection purpose string for off-site S3 access key id.</summary>
    public const string OffsiteAccessKeyProtectionPurpose = "ALDevToolbox.SystemSettings.OffsiteAccessKey";

    /// <summary>Data Protection purpose string for off-site S3 secret access key.</summary>
    public const string OffsiteSecretKeyProtectionPurpose = "ALDevToolbox.SystemSettings.OffsiteSecretKey";

    private readonly AppDbContext _db;
    private readonly IDataProtector _protector;
    private readonly IDataProtector _offsiteAccessProtector;
    private readonly IDataProtector _offsiteSecretProtector;
    private readonly ILogger<SystemSettingsService> _logger;
    private readonly TimeProvider _clock;

    public SystemSettingsService(
        AppDbContext db,
        IDataProtectionProvider protectionProvider,
        ILogger<SystemSettingsService> logger,
        TimeProvider clock)
    {
        _db = db;
        _protector = protectionProvider.CreateProtector(SmtpPasswordProtectionPurpose);
        _offsiteAccessProtector = protectionProvider.CreateProtector(OffsiteAccessKeyProtectionPurpose);
        _offsiteSecretProtector = protectionProvider.CreateProtector(OffsiteSecretKeyProtectionPurpose);
        _logger = logger;
        _clock = clock;
    }

    /// <summary>Loads the singleton row, populating the audit-friendly view.</summary>
    public async Task<SystemSettingsView> GetViewAsync(CancellationToken ct = default)
    {
        var row = await LoadAsync(ct);
        return new SystemSettingsView(
            SmtpHost: row.SmtpHost,
            SmtpPort: row.SmtpPort,
            SmtpUser: row.SmtpUser,
            HasSmtpPassword: !string.IsNullOrEmpty(row.SmtpPasswordEncrypted),
            SmtpFrom: row.SmtpFrom,
            SmtpUseStartTls: row.SmtpUseStartTls,
            BannerText: row.BannerText,
            DefaultSignupAutoApprove: row.DefaultSignupAutoApprove,
            BackupScheduleEnabled: row.BackupScheduleEnabled,
            BackupScheduleTimeUtc: row.BackupScheduleTimeUtc,
            BackupRetentionCount: row.BackupRetentionCount,
            PerTenantBackupRetentionCount: row.PerTenantBackupRetentionCount,
            DefaultStorageQuotaMb: row.DefaultStorageQuotaMb,
            IndexSizeMultiplier: row.IndexSizeMultiplier,
            McpEnabled: row.McpEnabled,
            UpdatedAt: row.UpdatedAt);
    }

    /// <summary>
    /// Persists changes from the SiteAdmin settings form. Validation: SMTP
    /// port (when supplied) must be 1–65535; SMTP From (when supplied) must
    /// look like an email address. Banner is unconstrained beyond a length
    /// cap. Throws <see cref="PlanValidationException"/> with field-keyed
    /// errors so the form can render them inline.
    /// </summary>
    public async Task SaveAsync(SystemSettingsInput input, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        if (input.SmtpPort is int port && (port < 1 || port > 65535))
        {
            errors["SmtpPort"] = "Port must be between 1 and 65535.";
        }
        if (!string.IsNullOrWhiteSpace(input.SmtpFrom)
            && (!input.SmtpFrom.Contains('@') || input.SmtpFrom.Length > 254))
        {
            errors["SmtpFrom"] = "Enter a valid email address.";
        }
        if (input.BannerText is { Length: > 500 })
        {
            errors["BannerText"] = "Banner text must be 500 characters or fewer.";
        }
        if (input.BackupRetentionCount < 1 || input.BackupRetentionCount > 365)
        {
            errors["BackupRetentionCount"] = "Retention count must be between 1 and 365.";
        }
        if (input.PerTenantBackupRetentionCount < 1 || input.PerTenantBackupRetentionCount > 365)
        {
            errors["PerTenantBackupRetentionCount"] = "Per-tenant retention must be between 1 and 365.";
        }
        if (input.DefaultStorageQuotaMb is int quota && quota < 0)
        {
            errors["DefaultStorageQuotaMb"] = "Default quota must be 0 or greater. Leave blank for unlimited.";
        }
        if (input.IndexSizeMultiplier < 0m || input.IndexSizeMultiplier > 10m)
        {
            errors["IndexSizeMultiplier"] = "Multiplier must be between 0 and 10.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var row = await LoadAsync(ct);

        row.SmtpHost = NullIfBlank(input.SmtpHost);
        row.SmtpPort = input.SmtpPort;
        row.SmtpUser = NullIfBlank(input.SmtpUser);
        row.SmtpFrom = NullIfBlank(input.SmtpFrom);
        row.SmtpUseStartTls = input.SmtpUseStartTls;
        row.BannerText = NullIfBlank(input.BannerText);
        row.DefaultSignupAutoApprove = input.DefaultSignupAutoApprove;
        row.BackupScheduleEnabled = input.BackupScheduleEnabled;
        row.BackupScheduleTimeUtc = input.BackupScheduleTimeUtc;
        row.BackupRetentionCount = input.BackupRetentionCount;
        row.PerTenantBackupRetentionCount = input.PerTenantBackupRetentionCount;
        row.DefaultStorageQuotaMb = input.DefaultStorageQuotaMb;
        row.IndexSizeMultiplier = input.IndexSizeMultiplier;
        row.McpEnabled = input.McpEnabled;
        row.UpdatedAt = _clock.GetUtcNow().UtcDateTime;

        if (input.ClearSmtpPassword)
        {
            row.SmtpPasswordEncrypted = null;
        }
        else if (!string.IsNullOrEmpty(input.SmtpPassword))
        {
            row.SmtpPasswordEncrypted = _protector.Protect(input.SmtpPassword);
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "System settings updated (smtp_host={SmtpHost}, banner={HasBanner}, auto_approve={AutoApprove}).",
            row.SmtpHost ?? "<unset>",
            !string.IsNullOrEmpty(row.BannerText),
            row.DefaultSignupAutoApprove);
    }

    /// <summary>
    /// Returns the SMTP configuration the email sender should use, preferring
    /// the DB-stored override when present and falling back to env vars when
    /// the DB row is unset. Returns <see langword="null"/> when neither path
    /// yields a host + from.
    /// </summary>
    public async Task<ResolvedSmtpSettings?> ResolveSmtpAsync(CancellationToken ct = default)
    {
        var row = await _db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, ct);
        return TryResolveFromDb(row) ?? ResolveFromEnv();
    }

    private ResolvedSmtpSettings? TryResolveFromDb(SystemSettings? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.SmtpHost) || string.IsNullOrWhiteSpace(row.SmtpFrom))
        {
            return null;
        }

        string? plaintext = null;
        if (!string.IsNullOrEmpty(row.SmtpPasswordEncrypted))
        {
            try
            {
                plaintext = _protector.Unprotect(row.SmtpPasswordEncrypted);
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                // Loud signal: a key-ring rotation that lost the old key would
                // otherwise silently degrade outbound mail.
                _logger.LogError(ex, "Failed to decrypt SMTP password from system_settings; falling back to env vars.");
                return null;
            }
        }

        return ResolvedSmtpSettings.TryFrom(
            host: row.SmtpHost,
            port: row.SmtpPort,
            user: row.SmtpUser,
            password: plaintext,
            from: row.SmtpFrom,
            useStartTls: row.SmtpUseStartTls);
    }

    private static ResolvedSmtpSettings? ResolveFromEnv() =>
        ResolvedSmtpSettings.TryFrom(
            host: Environment.GetEnvironmentVariable("SMTP_HOST"),
            port: int.TryParse(Environment.GetEnvironmentVariable("SMTP_PORT"), out var p) ? p : null,
            user: Environment.GetEnvironmentVariable("SMTP_USER"),
            password: ReadSecret("SMTP_PASSWORD_FILE"),
            from: Environment.GetEnvironmentVariable("SMTP_FROM"),
            useStartTls: ParseBool(Environment.GetEnvironmentVariable("SMTP_USE_STARTTLS")));

    /// <summary>Loads the off-site backup settings for the SiteAdmin form (no plaintext keys).</summary>
    public async Task<OffsiteSettingsView> GetOffsiteViewAsync(CancellationToken ct = default)
    {
        var row = await LoadAsync(ct);
        return new OffsiteSettingsView(
            Enabled: row.OffsiteBackupEnabled,
            Endpoint: row.OffsiteEndpoint,
            Region: row.OffsiteRegion,
            Bucket: row.OffsiteBucket,
            Prefix: row.OffsitePrefix,
            HasAccessKey: !string.IsNullOrEmpty(row.OffsiteAccessKeyEncrypted),
            HasSecretKey: !string.IsNullOrEmpty(row.OffsiteSecretKeyEncrypted),
            ForcePathStyle: row.OffsiteForcePathStyle,
            RetentionDays: row.OffsiteRetentionDays);
    }

    /// <summary>
    /// Persists off-site backup settings. When keys are supplied they're
    /// encrypted via the Data Protection ring; empty keys leave the stored
    /// value untouched. Throws <see cref="PlanValidationException"/> with
    /// field-keyed errors so the form can render them inline.
    /// </summary>
    public async Task SaveOffsiteAsync(OffsiteSettingsInput input, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        if (input.Enabled)
        {
            if (string.IsNullOrWhiteSpace(input.Bucket))
                errors["OffsiteBucket"] = "Bucket is required when off-site backup is enabled.";
        }
        if (input.RetentionDays < 1 || input.RetentionDays > 3650)
        {
            errors["OffsiteRetentionDays"] = "Off-site retention must be between 1 and 3650 days.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var row = await LoadAsync(ct);
        row.OffsiteBackupEnabled = input.Enabled;
        row.OffsiteEndpoint = NullIfBlank(input.Endpoint);
        row.OffsiteRegion = NullIfBlank(input.Region);
        row.OffsiteBucket = NullIfBlank(input.Bucket);
        row.OffsitePrefix = NullIfBlank(input.Prefix);
        row.OffsiteForcePathStyle = input.ForcePathStyle;
        row.OffsiteRetentionDays = input.RetentionDays;

        if (input.ClearAccessKey) row.OffsiteAccessKeyEncrypted = null;
        else if (!string.IsNullOrEmpty(input.AccessKey))
            row.OffsiteAccessKeyEncrypted = _offsiteAccessProtector.Protect(input.AccessKey);

        if (input.ClearSecretKey) row.OffsiteSecretKeyEncrypted = null;
        else if (!string.IsNullOrEmpty(input.SecretKey))
            row.OffsiteSecretKeyEncrypted = _offsiteSecretProtector.Protect(input.SecretKey);

        row.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Off-site backup settings updated (enabled={Enabled}, bucket={Bucket}).",
            row.OffsiteBackupEnabled, row.OffsiteBucket ?? "<unset>");
    }

    /// <summary>
    /// Decrypts the stored credentials and returns a fully resolved
    /// configuration ready for the S3 SDK. Returns <see langword="null"/>
    /// when off-site is disabled, the bucket isn't set, or either key is
    /// missing / undecryptable.
    /// </summary>
    public async Task<ResolvedOffsiteSettings?> ResolveOffsiteAsync(CancellationToken ct = default)
    {
        var row = await _db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (row is null || !row.OffsiteBackupEnabled) return null;
        if (string.IsNullOrWhiteSpace(row.OffsiteBucket)) return null;
        if (string.IsNullOrEmpty(row.OffsiteAccessKeyEncrypted) || string.IsNullOrEmpty(row.OffsiteSecretKeyEncrypted)) return null;
        string accessKey, secretKey;
        try
        {
            accessKey = _offsiteAccessProtector.Unprotect(row.OffsiteAccessKeyEncrypted);
            secretKey = _offsiteSecretProtector.Unprotect(row.OffsiteSecretKeyEncrypted);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            _logger.LogError(ex, "Failed to decrypt off-site credentials; off-site backup disabled until re-entered.");
            return null;
        }
        return new ResolvedOffsiteSettings(
            Endpoint: NullIfBlank(row.OffsiteEndpoint),
            Region: NullIfBlank(row.OffsiteRegion),
            Bucket: row.OffsiteBucket!,
            Prefix: NullIfBlank(row.OffsitePrefix),
            AccessKey: accessKey,
            SecretKey: secretKey,
            ForcePathStyle: row.OffsiteForcePathStyle,
            RetentionDays: row.OffsiteRetentionDays);
    }

    /// <summary>
    /// Returns the system banner text, or <see langword="null"/> when none is
    /// set. Cached per-request via <see cref="AppDbContext"/>'s scoped
    /// lifetime; SiteAdmin updates land on the next request.
    /// </summary>
    public async Task<string?> GetBannerAsync(CancellationToken ct = default)
    {
        var row = await _db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, ct);
        return string.IsNullOrWhiteSpace(row?.BannerText) ? null : row!.BannerText;
    }

    /// <summary>True when admin approval should be skipped for new signups into existing organisations.</summary>
    public async Task<bool> ShouldAutoApproveSignupAsync(CancellationToken ct = default)
    {
        var row = await _db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, ct);
        return row?.DefaultSignupAutoApprove ?? false;
    }

    /// <summary>
    /// True when the SiteAdmin has enabled the MCP server on this
    /// deployment. The MCP endpoint and the Tools menu's "MCP" link both
    /// hide themselves when this returns <c>false</c>, regardless of the
    /// deployment-level <c>Mcp:Enabled</c> in appsettings.
    /// </summary>
    public async Task<bool> IsMcpEnabledAsync(CancellationToken ct = default)
    {
        return await _db.SystemSettings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.McpEnabled)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<SystemSettings> LoadAsync(CancellationToken ct)
    {
        var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (row is null)
        {
            // Defensive: the migration inserts the singleton row, but tests
            // that bypass migrations (or future databases that don't run the
            // seed Sql) shouldn't NRE. Insert-once-on-demand keeps GetViewAsync
            // and SaveAsync robust without changing the deployment story.
            row = new SystemSettings
            {
                Id = 1,
                DefaultSignupAutoApprove = false,
                UpdatedAt = _clock.GetUtcNow().UtcDateTime,
            };
            _db.SystemSettings.Add(row);
            await _db.SaveChangesAsync(ct);
        }
        return row;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool? ParseBool(string? value) =>
        string.IsNullOrEmpty(value) ? null : value.Equals("true", StringComparison.OrdinalIgnoreCase);

    private static string? ReadSecret(string envVarName)
    {
        var path = Environment.GetEnvironmentVariable(envVarName);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        return File.ReadAllText(path).Trim();
    }
}
