namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One row per per-tenant logical backup written under
/// <c>{BackupsDirectory}/tenants/{slug}/</c>. Per-tenant backups exist so
/// SiteAdmins can answer "please restore my org to yesterday" without
/// touching any other tenant. The format is JSON-per-table inside a ZIP,
/// schema-versioned via <see cref="SchemaVersion"/>; restore runs in a
/// single transaction that deletes the org's rows in FK-reverse order
/// then re-inserts from the snapshot.
/// </summary>
public class PerTenantBackup
{
    public int Id { get; set; }

    /// <summary>Organisation the backup belongs to.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Filename inside the per-tenant directory — no path separators.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Size of the ZIP on disk at write time.</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>UTC instant the snapshot was taken.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>SiteAdmin who triggered an ad-hoc backup. Null for scheduled runs.</summary>
    public int? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    public BackupKind Kind { get; set; }

    /// <summary>
    /// Snapshot format version baked into the ZIP. Restore refuses a
    /// snapshot whose version doesn't match the current code so a
    /// post-migration restore of a pre-migration backup fails loudly
    /// instead of silently dropping unknown rows.
    /// </summary>
    public int SchemaVersion { get; set; }

    /// <summary>Pinned snapshots are exempt from retention pruning.</summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// UTC instant the snapshot ZIP was last uploaded to the off-site
    /// bucket, or <see langword="null"/> if it has never been mirrored.
    /// Set by <see cref="Services.OffsiteBackupService.UploadPerTenantAsync"/>.
    /// </summary>
    public DateTime? OffsiteUploadedAt { get; set; }

    /// <summary>
    /// Object key (including the configured prefix) under which the ZIP
    /// lives in the off-site bucket. Set together with
    /// <see cref="OffsiteUploadedAt"/>.
    /// </summary>
    public string? OffsiteObjectKey { get; set; }
}
