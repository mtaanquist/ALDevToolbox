using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Builds a <see cref="SystemSettingsInput"/> by overlaying the fields
/// for one settings section onto the values already in the database.
/// The split lets each settings sub-page POST only its own fields while
/// still calling <see cref="SystemSettingsService.SaveAsync"/>, which
/// does the single-row write and validation.
/// </summary>
internal static class SettingsInputBuilder
{
    public static SystemSettingsInput WithSmtp(SystemSettingsView current, IFormCollection form) => Base(current) with
    {
        SmtpHost = form["SmtpHost"].ToString(),
        SmtpPort = int.TryParse(form["SmtpPort"], out var port) ? port : null,
        SmtpUser = form["SmtpUser"].ToString(),
        SmtpPassword = form["SmtpPassword"].ToString(),
        ClearSmtpPassword = IsChecked(form, "ClearSmtpPassword"),
        SmtpFrom = form["SmtpFrom"].ToString(),
        SmtpFromName = form["SmtpFromName"].ToString(),
        SmtpUseStartTls = form.ContainsKey("SmtpUseStartTls") ? IsChecked(form, "SmtpUseStartTls") : null,
    };

    public static SystemSettingsInput WithBackups(SystemSettingsView current, IFormCollection form) => Base(current) with
    {
        BackupScheduleEnabled = IsChecked(form, "BackupScheduleEnabled"),
        BackupScheduleTimeUtc = TimeOnly.TryParse(form["BackupScheduleTimeUtc"], out var bst)
            ? bst
            : current.BackupScheduleTimeUtc,
        BackupRetentionCount = int.TryParse(form["BackupRetentionCount"], out var brc)
            ? brc
            : current.BackupRetentionCount,
        PerTenantBackupRetentionCount = int.TryParse(form["PerTenantBackupRetentionCount"], out var ptrc)
            ? ptrc
            : current.PerTenantBackupRetentionCount,
    };

    public static SystemSettingsInput WithQuotas(SystemSettingsView current, IFormCollection form) => Base(current) with
    {
        DefaultStorageQuotaMb = string.IsNullOrWhiteSpace(form["DefaultStorageQuotaMb"])
            ? null
            : int.TryParse(form["DefaultStorageQuotaMb"], out var dsq) ? dsq : null,
        IndexSizeMultiplier = decimal.TryParse(
            form["IndexSizeMultiplier"],
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var ism)
                ? ism
                : current.IndexSizeMultiplier,
    };

    public static SystemSettingsInput WithGeneral(SystemSettingsView current, IFormCollection form) => Base(current) with
    {
        BannerText = form["BannerText"].ToString(),
        DefaultSignupAutoApprove = IsChecked(form, "DefaultSignupAutoApprove"),
        SignupEmailDomainAllowlist = form["SignupEmailDomainAllowlist"].ToString(),
        ReleaseDownloadDomainAllowlist = form["ReleaseDownloadDomainAllowlist"].ToString(),
    };

    /// <summary>
    /// Overlays the Tools tab. Each tool has a checkbox <c>tool_&lt;Key&gt;</c> that's
    /// checked when the tool is <em>on</em>; an unchecked (so unposted) box means
    /// the tool is disabled. MCP is part of the same grid but maps to
    /// <see cref="SystemSettingsInput.McpEnabled"/>, not the disabled set.
    /// </summary>
    public static SystemSettingsInput WithTools(SystemSettingsView current, IFormCollection form) => Base(current) with
    {
        McpEnabled = IsChecked(form, $"tool_{ToolKey.Mcp}"),
        DisabledTools = ToolCatalog.All
            .Where(t => t.Key != ToolKey.Mcp && !IsChecked(form, $"tool_{t.Key}"))
            .Select(t => t.Key)
            .ToList(),
    };

    /// <summary>
    /// Carries every field from the current view across into an Input —
    /// the per-section overlays then use <c>with</c> to mutate just the
    /// fields they own. SMTP password is intentionally left empty +
    /// ClearSmtpPassword=false so a non-SMTP save never disturbs the
    /// encrypted password column.
    /// </summary>
    private static SystemSettingsInput Base(SystemSettingsView current) => new(
        SmtpHost: current.SmtpHost,
        SmtpPort: current.SmtpPort,
        SmtpUser: current.SmtpUser,
        SmtpPassword: null,
        ClearSmtpPassword: false,
        SmtpFrom: current.SmtpFrom,
        SmtpFromName: current.SmtpFromName,
        SmtpUseStartTls: current.SmtpUseStartTls,
        BannerText: current.BannerText,
        DefaultSignupAutoApprove: current.DefaultSignupAutoApprove,
        BackupScheduleEnabled: current.BackupScheduleEnabled,
        BackupScheduleTimeUtc: current.BackupScheduleTimeUtc,
        BackupRetentionCount: current.BackupRetentionCount,
        PerTenantBackupRetentionCount: current.PerTenantBackupRetentionCount,
        DefaultStorageQuotaMb: current.DefaultStorageQuotaMb,
        IndexSizeMultiplier: current.IndexSizeMultiplier,
        McpEnabled: current.McpEnabled,
        SignupEmailDomainAllowlist: current.SignupEmailDomainAllowlist,
        ReleaseDownloadDomainAllowlist: current.ReleaseDownloadDomainAllowlist,
        DisabledTools: ToolCatalog.ParseDisabled(current.DisabledTools).ToList());

    private static bool IsChecked(IFormCollection form, string name)
    {
        var raw = form[name].ToString();
        return raw == "true" || raw == "on";
    }
}
