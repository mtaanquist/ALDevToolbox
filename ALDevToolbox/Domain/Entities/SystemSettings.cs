namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Singleton row (id pinned to <c>1</c>) holding cross-organisation
/// configuration: SMTP override, system banner, and the default signup
/// approval policy. The SMTP password is stored as Data-Protection
/// ciphertext; the audit interceptor redacts the column to a fixed
/// sentinel rather than capturing ciphertext history.
/// </summary>
public class SystemSettings
{
    /// <summary>Pinned to <c>1</c>. The migration inserts the row; nothing else creates one.</summary>
    public int Id { get; set; }

    /// <summary>SMTP host override. Empty/null means "fall back to <c>SMTP_HOST</c> env var".</summary>
    public string? SmtpHost { get; set; }

    public int? SmtpPort { get; set; }

    public string? SmtpUser { get; set; }

    /// <summary>
    /// Data-Protection-encrypted SMTP password. Decryption is contained in
    /// <see cref="Services.SystemSettingsService"/>; plaintext never
    /// leaves that service boundary.
    /// </summary>
    public string? SmtpPasswordEncrypted { get; set; }

    public string? SmtpFrom { get; set; }

    public bool? SmtpUseStartTls { get; set; }

    /// <summary>Free-text banner displayed at the top of every page when set.</summary>
    public string? BannerText { get; set; }

    /// <summary>
    /// When <see langword="true"/>, signups into existing organisations are
    /// auto-approved without an org admin's review. Defaults to
    /// <see langword="false"/> — admin approval is the safer default for v1.
    /// </summary>
    public bool DefaultSignupAutoApprove { get; set; }

    /// <summary>
    /// When <see langword="true"/>, <c>BackupScheduler</c> takes a daily
    /// backup at <see cref="BackupScheduleTimeUtc"/>. Operators can pause
    /// the schedule without losing the time-of-day setting.
    /// </summary>
    public bool BackupScheduleEnabled { get; set; } = true;

    /// <summary>
    /// UTC time-of-day for the daily backup. Stored as
    /// <see cref="TimeOnly"/> so the column type is <c>time</c>, which
    /// drops the timezone-aware drift that plagued the original
    /// timestamp-with-time-zone shape during prototyping.
    /// </summary>
    public TimeOnly BackupScheduleTimeUtc { get; set; } = new(2, 0);

    /// <summary>
    /// Number of unpinned backups retained on disk. Older unpinned files
    /// are pruned after each successful backup. Pinned backups are
    /// exempt and never counted toward this cap.
    /// </summary>
    public int BackupRetentionCount { get; set; } = 14;

    /// <summary>
    /// Number of per-tenant snapshots retained on disk per organisation.
    /// Independent of <see cref="BackupRetentionCount"/> so SiteAdmins can
    /// keep a longer "restore to yesterday" tail (e.g. 30 days) without
    /// holding 30 full pg_dumps. Pinned snapshots are exempt.
    /// </summary>
    public int PerTenantBackupRetentionCount { get; set; } = 30;

    /// <summary>
    /// Default storage quota (in megabytes) applied to organisations that
    /// have no per-org override. Null means unlimited. The
    /// <c>StorageQuotaGuard</c> hard-blocks tenant writes once the
    /// organisation's billable usage reaches the effective quota.
    /// </summary>
    public int? DefaultStorageQuotaMb { get; set; }

    /// <summary>
    /// Weight applied to per-org index/metadata bytes when computing the
    /// billable size for quota checks: <c>billable = logical + multiplier *
    /// index</c>. Lets operators charge primarily for logical data while
    /// still soft-accounting for index/metadata overhead. Default 0.5.
    /// </summary>
    public decimal IndexSizeMultiplier { get; set; } = 0.5m;

    public DateTime UpdatedAt { get; set; }
}
