using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Offsite;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Result of <see cref="OffsiteBackupService.TestConnectionAsync"/>. The
/// settings form renders the message inline so SiteAdmins can verify the
/// credentials before flipping the schedule on. Shared with the storage
/// providers in <see cref="Offsite"/>, which produce backend-specific messages.
/// </summary>
public sealed record OffsiteTestResult(bool Success, string Message);

/// <summary>
/// One object returned by <see cref="OffsiteBackupService.ListAsync"/>.
/// The DR-restore catalogue on <c>/site-admin/backups</c> renders these
/// directly. <c>FileName</c> is the catalogue's natural identity — what
/// the row will be stored under locally; <c>Key</c> includes the bucket
/// prefix and is the value the download endpoint takes back in.
/// </summary>
public sealed record OffsiteObjectInfo(string Key, string FileName, long Size, DateTime LastModified);

/// <summary>
/// One per-tenant snapshot object returned by
/// <see cref="OffsiteBackupService.ListPerTenantAsync"/>. Carries the slug
/// parsed out of the object key so the catalogue UI can group rows by
/// organisation without an extra round-trip.
/// </summary>
public sealed record OffsitePerTenantObjectInfo(string Key, string Slug, string FileName, long Size, DateTime LastModified);

/// <summary>
/// Uploads full-database pg_dump backups and per-tenant snapshot ZIPs to
/// the configured off-site object store and prunes objects older than
/// <c>OffsiteRetentionDays</c>. Whole-DB dumps live at
/// <c>&lt;prefix&gt;&lt;filename&gt;</c>; per-tenant snapshots namespace
/// under <c>&lt;prefix&gt;tenants/&lt;slug&gt;/&lt;filename&gt;</c> so the
/// two catalogues stay separable and a DR restore can pull either back
/// down independently. After a whole-deployment loss, the per-tenant
/// copies are what keeps the "restore one org to yesterday" surface alive.
///
/// <para>
/// The backend (S3-compatible or Azure Blob) is chosen per request from the
/// resolved settings via <see cref="IOffsiteStorageProviderFactory"/>. This
/// service owns all orchestration — object-key shapes, <c>.partial</c>
/// staging, the deployment-id fingerprint gate, retention filtering, and DB
/// bookkeeping — so both backends behave identically; only the raw transport
/// lives behind <see cref="IOffsiteStorageProvider"/>.
/// </para>
/// </summary>
public sealed class OffsiteBackupService
{
    /// <summary>Sub-prefix under the configured bucket prefix where per-tenant snapshots live.</summary>
    public const string PerTenantKeyPrefix = "tenants/";

    /// <summary>
    /// User-metadata key stamped on whole-DB dumps so a restore can verify the
    /// dump came from this deployment rather than a neighbour sharing the
    /// bucket. Providers normalise the key to their backend's rules (S3 sends
    /// it as <c>x-amz-meta-deployment-id</c>; Azure stores <c>deploymentid</c>)
    /// and map it back on read, so this canonical form is all the service sees.
    /// </summary>
    public const string DeploymentMetadataKey = "deployment-id";

    /// <summary>Upper bound for the prune listing — effectively "every object under the prefix".</summary>
    private const int PruneListCap = int.MaxValue;

    /// <summary>
    /// Number of most-recent whole-DB dumps the off-site prune keeps regardless
    /// of age. Off-site has no pin concept (unlike local backups, where
    /// DownloadAsync auto-pins a freshly-pulled DR snapshot), so without this an
    /// operator's most recent disaster-recovery dump could be deleted by the next
    /// scheduler tick mid-restore. See issue #380.
    /// </summary>
    private const int PruneKeepRecent = 3;

    private readonly AppDbContext _db;
    private readonly SystemSettingsService _systemSettings;
    private readonly BackupService _backups;
    private readonly PerTenantBackupService _perTenantBackups;
    private readonly IOffsiteStorageProviderFactory _providerFactory;
    private readonly ILogger<OffsiteBackupService> _logger;
    private readonly TimeProvider _clock;
    private readonly DeploymentIdentity _deployment;

    public OffsiteBackupService(
        AppDbContext db,
        SystemSettingsService systemSettings,
        BackupService backups,
        PerTenantBackupService perTenantBackups,
        IOffsiteStorageProviderFactory providerFactory,
        ILogger<OffsiteBackupService> logger,
        TimeProvider clock,
        DeploymentIdentity deployment)
    {
        _db = db;
        _systemSettings = systemSettings;
        _backups = backups;
        _perTenantBackups = perTenantBackups;
        _providerFactory = providerFactory;
        _logger = logger;
        _clock = clock;
        _deployment = deployment;
    }

    /// <summary>Reachability + auth check against the configured bucket/container. Returns a structured result for the UI to render inline.</summary>
    public async Task<OffsiteTestResult> TestConnectionAsync(CancellationToken ct)
    {
        var settings = await _systemSettings.ResolveOffsiteAsync(ct);
        if (settings is null)
        {
            return new OffsiteTestResult(false, "Off-site backup is not fully configured. Save the form with credentials and a bucket first.");
        }
        var provider = _providerFactory.Create(settings);
        try
        {
            return await provider.TestConnectionAsync(ct);
        }
        finally
        {
            DisposeProvider(provider);
        }
    }

    /// <summary>
    /// Uploads a single backup row's file to the configured store and stamps
    /// the row with <c>OffsiteUploadedAt</c> + <c>OffsiteObjectKey</c>. No-op
    /// when off-site is disabled or the file is missing locally.
    /// </summary>
    public async Task<string?> UploadAsync(int backupId, CancellationToken ct)
    {
        var settings = await _systemSettings.ResolveOffsiteAsync(ct);
        if (settings is null) return null;

        var row = await _db.Backups.FirstOrDefaultAsync(b => b.Id == backupId, ct);
        if (row is null) return null;

        var path = Path.Combine(_backups.BackupsDirectory, row.FileName);
        if (!File.Exists(path))
        {
            _logger.LogWarning("Refusing to upload {FileName}: local file missing.", row.FileName);
            return null;
        }

        var objectKey = BuildObjectKey(settings.Prefix, row.FileName);
        var provider = _providerFactory.Create(settings);
        try
        {
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true))
            {
                // Fingerprint the dump with this deployment's id so a later restore
                // can refuse a neighbour deployment's dump found under the same prefix.
                var metadata = new Dictionary<string, string>(StringComparer.Ordinal) { [DeploymentMetadataKey] = _deployment.Id };
                await provider.UploadAsync(objectKey, stream, "application/octet-stream", metadata, ct);
            }

            row.OffsiteUploadedAt = _clock.GetUtcNow().UtcDateTime;
            row.OffsiteObjectKey = objectKey;
            await SaveOrCleanupOrphanAsync(provider, objectKey, ct);
        }
        finally
        {
            DisposeProvider(provider);
        }

        _logger.LogInformation(
            "Uploaded backup {FileName} to off-site object {Bucket}/{Key}.",
            row.FileName, settings.Bucket, objectKey);
        return objectKey;
    }

    /// <summary>
    /// Uploads a per-tenant snapshot ZIP to
    /// <c>&lt;prefix&gt;tenants/&lt;slug&gt;/&lt;filename&gt;</c> and stamps
    /// the row with <c>OffsiteUploadedAt</c> + <c>OffsiteObjectKey</c>. No-op
    /// when off-site is disabled, the row is missing, or the local ZIP has
    /// been removed out-of-band.
    /// </summary>
    public async Task<string?> UploadPerTenantAsync(int perTenantBackupId, CancellationToken ct)
    {
        var settings = await _systemSettings.ResolveOffsiteAsync(ct);
        if (settings is null) return null;

        var row = await _db.PerTenantBackups
            .Include(b => b.Organization)
            .FirstOrDefaultAsync(b => b.Id == perTenantBackupId, ct);
        if (row is null || row.Organization is null) return null;

        var path = Path.Combine(_perTenantBackups.DirectoryFor(row.Organization.Slug), row.FileName);
        if (!File.Exists(path))
        {
            _logger.LogWarning(
                "Refusing to upload per-tenant snapshot {FileName} for {Slug}: local file missing.",
                row.FileName, row.Organization.Slug);
            return null;
        }

        var objectKey = BuildPerTenantObjectKey(settings.Prefix, row.Organization.Slug, row.FileName);
        var provider = _providerFactory.Create(settings);
        try
        {
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true))
            {
                // Per-tenant snapshots carry no deployment-id stamp: the manifest
                // inside the ZIP already names the owning org, and the download path
                // verifies it.
                await provider.UploadAsync(objectKey, stream, "application/zip",
                    EmptyMetadata, ct);
            }

            row.OffsiteUploadedAt = _clock.GetUtcNow().UtcDateTime;
            row.OffsiteObjectKey = objectKey;
            await SaveOrCleanupOrphanAsync(provider, objectKey, ct);
        }
        finally
        {
            DisposeProvider(provider);
        }

        _logger.LogInformation(
            "Uploaded per-tenant snapshot {FileName} for {Slug} to off-site object {Bucket}/{Key}.",
            row.FileName, row.Organization.Slug, settings.Bucket, objectKey);
        return objectKey;
    }

    /// <summary>
    /// Lists every dump-shaped object under the configured prefix, newest
    /// first. Drives the off-site catalogue on <c>/site-admin/backups</c>;
    /// returns an empty list when off-site isn't configured rather than
    /// throwing, so the page renders the "not configured" hint cleanly.
    /// </summary>
    /// <param name="maxObjects">Upper bound on results returned. The
    /// catalogue is meant for picking a recent DR snapshot, not for paging
    /// a giant bucket — a small cap keeps the page fast and the bill
    /// predictable.</param>
    public async Task<IReadOnlyList<OffsiteObjectInfo>> ListAsync(int maxObjects, CancellationToken ct)
    {
        var settings = await _systemSettings.ResolveOffsiteAsync(ct);
        if (settings is null) return Array.Empty<OffsiteObjectInfo>();
        if (maxObjects < 1) maxObjects = 1;

        var prefix = settings.Prefix ?? string.Empty;
        var provider = _providerFactory.Create(settings);
        IReadOnlyList<OffsiteStorageObject> objects;
        try
        {
            objects = await provider.ListAsync(prefix, maxObjects, ct);
        }
        finally
        {
            DisposeProvider(provider);
        }

        var results = new List<OffsiteObjectInfo>(capacity: Math.Min(maxObjects, 256));
        foreach (var obj in objects)
        {
            if (!IsWholeDbDumpKey(obj.Key, prefix, out var fileName)) continue;
            results.Add(new OffsiteObjectInfo(obj.Key, fileName, obj.Size, obj.LastModifiedUtc));
            if (results.Count >= maxObjects) break;
        }

        results.Sort((a, b) => b.LastModified.CompareTo(a.LastModified));
        return results;
    }

    /// <summary>
    /// Lists every per-tenant snapshot under
    /// <c>&lt;prefix&gt;tenants/&lt;slug&gt;/</c>, newest first. Drives the
    /// off-site catalogue on <c>/site-admin/tenant-backups</c>; returns an
    /// empty list when off-site isn't configured.
    /// </summary>
    public async Task<IReadOnlyList<OffsitePerTenantObjectInfo>> ListPerTenantAsync(int maxObjects, CancellationToken ct)
    {
        var settings = await _systemSettings.ResolveOffsiteAsync(ct);
        if (settings is null) return Array.Empty<OffsitePerTenantObjectInfo>();
        if (maxObjects < 1) maxObjects = 1;

        var prefix = settings.Prefix ?? string.Empty;
        var tenantPrefix = (prefix.Length == 0 ? string.Empty : EnsureTrailingSlash(prefix)) + PerTenantKeyPrefix;
        var provider = _providerFactory.Create(settings);
        IReadOnlyList<OffsiteStorageObject> objects;
        try
        {
            objects = await provider.ListAsync(tenantPrefix, maxObjects, ct);
        }
        finally
        {
            DisposeProvider(provider);
        }

        var results = new List<OffsitePerTenantObjectInfo>(capacity: Math.Min(maxObjects, 256));
        foreach (var obj in objects)
        {
            var remainder = StripPrefix(obj.Key, tenantPrefix);
            // Expected shape: "<slug>/<filename>.tenant.zip". Reject
            // anything with extra path segments or the wrong suffix.
            var slash = remainder.IndexOf('/');
            if (slash <= 0 || slash == remainder.Length - 1) continue;
            var slug = remainder[..slash];
            var fileName = remainder[(slash + 1)..];
            if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..", StringComparison.Ordinal)) continue;
            if (!fileName.EndsWith(PerTenantBackupService.FileSuffix, StringComparison.Ordinal)) continue;
            results.Add(new OffsitePerTenantObjectInfo(
                obj.Key, slug, fileName, obj.Size, obj.LastModifiedUtc));
            if (results.Count >= maxObjects) break;
        }

        results.Sort((a, b) => b.LastModified.CompareTo(a.LastModified));
        return results;
    }

    /// <summary>
    /// Downloads a single object from the store into the local backups
    /// directory and inserts a <c>backups</c> row pointing at it so the
    /// existing Restore button can take over. Reports byte-level progress
    /// via <paramref name="progress"/> so the SiteAdmin page can render a
    /// progress bar.
    ///
    /// <para>
    /// Refuses to overwrite an existing local file with the same name —
    /// the caller (a worker job) surfaces that as a Failed status rather
    /// than silently clobbering a row that may already be on disk. The
    /// downloaded file is staged under <c>&lt;name&gt;.partial</c> and
    /// renamed atomically on completion so a crash mid-download doesn't
    /// leave a half-written dump masquerading as a valid backup.
    /// </para>
    /// </summary>
    /// <returns>The id of the inserted <c>backups</c> row.</returns>
    public async Task<int> DownloadAsync(string objectKey, IProgress<(long BytesDownloaded, long? TotalBytes)>? progress, CancellationToken ct)
    {
        var settings = await _systemSettings.ResolveOffsiteAsync(ct)
            ?? throw new InvalidOperationException("Off-site backup is not configured.");

        var fileName = StripPrefix(objectKey, settings.Prefix ?? string.Empty);
        if (string.IsNullOrEmpty(fileName)
            || fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to download object with suspicious leaf name: {objectKey}");
        }
        if (!fileName.EndsWith(BackupService.BackupFileSuffix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to download non-dump object: {objectKey}");
        }

        Directory.CreateDirectory(_backups.BackupsDirectory);
        var finalPath = Path.Combine(_backups.BackupsDirectory, fileName);
        var stagingPath = finalPath + ".partial";

        if (File.Exists(finalPath))
        {
            throw new InvalidOperationException(
                $"A local backup named '{fileName}' already exists. Delete or rename it before re-importing the off-site copy.");
        }
        if (File.Exists(stagingPath))
        {
            // Stale staging file from a prior aborted attempt. Safe to remove
            // because *.partial is never linked to a backups row.
            File.Delete(stagingPath);
        }

        var provider = _providerFactory.Create(settings);
        try
        {
            await using var download = await provider.OpenReadAsync(objectKey, ct);

            // Provenance: refuse a dump another deployment stamped. A missing stamp
            // is treated as legacy (uploaded before fingerprinting existed) and
            // allowed with a warning; enforcement is skipped when our own id is
            // ephemeral, since it would otherwise spuriously reject after a restart.
            download.Metadata.TryGetValue(DeploymentMetadataKey, out var stampedDeployment);
            if (_deployment.IsPersistent && !string.IsNullOrEmpty(stampedDeployment)
                && !string.Equals(stampedDeployment, _deployment.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Refusing to import '{fileName}': it was created by a different deployment " +
                    "(deployment-id mismatch). Restoring it would overwrite this database with " +
                    "another deployment's data.");
            }
            if (string.IsNullOrEmpty(stampedDeployment))
            {
                _logger.LogWarning(
                    "Off-site object {Key} carries no deployment-id stamp; importing it anyway (legacy upload). " +
                    "Verify it belongs to this deployment before restoring.", objectKey);
            }

            await StreamToStagingAsync(download.Body, download.ContentLength, stagingPath, progress, ct);
        }
        finally
        {
            DisposeProvider(provider);
        }

        File.Move(stagingPath, finalPath);
        var size = new FileInfo(finalPath).Length;

        // Best-effort timestamp parse from our own filename shape:
        // aldevtoolbox-yyyyMMddTHHmmssZ-(scheduled|adhoc).dump
        var createdAt = TryParseTimestamp(fileName) ?? _clock.GetUtcNow().UtcDateTime;
        var row = new Backup
        {
            FileName = fileName,
            FileSizeBytes = size,
            CreatedAt = createdAt,
            // Downloaded files are operator-driven, even when the original
            // upload was scheduled. Marking AdHoc keeps the scheduled-backup
            // "latest scheduled at" decision matrix honest.
            Kind = BackupKind.AdHoc,
            // Auto-pin so the next retention sweep can't bin a DR snapshot
            // that the operator just hauled down. They can unpin
            // after the restore.
            IsPinned = true,
            OffsiteObjectKey = objectKey,
            OffsiteUploadedAt = _clock.GetUtcNow().UtcDateTime,
        };
        _db.Backups.Add(row);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            // Don't leave a downloaded dump on disk with no backups row — a
            // retry would otherwise trip the "a local backup already exists"
            // guard and force a manual cleanup.
            TryDeleteFile(finalPath);
            throw;
        }

        _logger.LogInformation(
            "Downloaded off-site backup {Bucket}/{Key} ({Bytes} bytes) into local row {BackupId}.",
            settings.Bucket, objectKey, size, row.Id);
        return row.Id;
    }

    /// <summary>
    /// Downloads a per-tenant snapshot ZIP into the per-tenant subdirectory
    /// and inserts a <c>per_tenant_backups</c> row pointing at it, so the
    /// existing Restore button on <c>/site-admin/tenant-backups</c> can take
    /// over. Mirror of <see cref="DownloadAsync"/> for the per-tenant case;
    /// reports byte-level progress for the same progress-bar surface.
    ///
    /// <para>
    /// Reads the snapshot's <c>manifest.json</c> to determine the owning
    /// organisation and refuses if the slug parsed from the object key
    /// doesn't match, the org isn't present locally, or the snapshot's
    /// <c>organization_id</c> can't be reconciled with the local org id.
    /// Stages under <c>&lt;name&gt;.partial</c> and renames atomically so a
    /// crash mid-download can't register a corrupt row.
    /// </para>
    /// </summary>
    /// <returns>The id of the inserted <c>per_tenant_backups</c> row.</returns>
    public async Task<int> DownloadPerTenantAsync(string objectKey, IProgress<(long BytesDownloaded, long? TotalBytes)>? progress, CancellationToken ct)
    {
        var settings = await _systemSettings.ResolveOffsiteAsync(ct)
            ?? throw new InvalidOperationException("Off-site backup is not configured.");

        var prefix = settings.Prefix ?? string.Empty;
        var tenantPrefix = (prefix.Length == 0 ? string.Empty : EnsureTrailingSlash(prefix)) + PerTenantKeyPrefix;
        var remainder = StripPrefix(objectKey, tenantPrefix);
        var slash = remainder.IndexOf('/');
        if (slash <= 0 || slash == remainder.Length - 1)
        {
            throw new InvalidOperationException($"Refusing to download per-tenant object with malformed key: {objectKey}");
        }
        var slug = remainder[..slash];
        var fileName = remainder[(slash + 1)..];
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to download per-tenant object with suspicious leaf name: {objectKey}");
        }
        if (!fileName.EndsWith(PerTenantBackupService.FileSuffix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to download non-per-tenant-snapshot object: {objectKey}");
        }

        // The slug must already exist locally — restoring a tenant snapshot
        // into a brand-new org would tangle FK references the manifest
        // doesn't know about. Look it up before downloading so we fail fast.
        var org = await _db.Organizations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Slug == slug, ct)
            ?? throw new InvalidOperationException(
                $"No local organisation with slug '{slug}' — create or restore the org before pulling its per-tenant snapshot.");

        var dir = _perTenantBackups.DirectoryFor(slug);
        Directory.CreateDirectory(dir);
        var finalPath = Path.Combine(dir, fileName);
        var stagingPath = finalPath + ".partial";

        if (File.Exists(finalPath))
        {
            throw new InvalidOperationException(
                $"A local per-tenant snapshot named '{fileName}' already exists for org '{slug}'. Delete or rename it before re-importing the off-site copy.");
        }
        if (File.Exists(stagingPath))
        {
            File.Delete(stagingPath);
        }

        var provider = _providerFactory.Create(settings);
        try
        {
            await using var download = await provider.OpenReadAsync(objectKey, ct);
            await StreamToStagingAsync(download.Body, download.ContentLength, stagingPath, progress, ct);
        }
        finally
        {
            DisposeProvider(provider);
        }

        // Verify the manifest before promoting the staging file to a row —
        // an object that doesn't carry a manifest, or whose manifest names
        // a different org, is either corrupt or someone else's snapshot.
        PerTenantBackupManifest manifest;
        await using (var zipStream = new FileStream(stagingPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            manifest = await PerTenantBackupService.ReadManifestAsync(zipStream, ct);
        }
        if (!string.Equals(manifest.organization_slug, slug, StringComparison.Ordinal))
        {
            File.Delete(stagingPath);
            throw new InvalidOperationException(
                $"Snapshot manifest names org '{manifest.organization_slug}' but the object key is under '{slug}'.");
        }

        File.Move(stagingPath, finalPath);
        var size = new FileInfo(finalPath).Length;

        var row = new PerTenantBackup
        {
            OrganizationId = org.Id,
            FileName = fileName,
            FileSizeBytes = size,
            CreatedAt = manifest.created_at,
            Kind = BackupKind.AdHoc,
            SchemaVersion = manifest.schema_version,
            // Auto-pin so retention can't bin the row before the operator
            // restores from it.
            IsPinned = true,
            OffsiteObjectKey = objectKey,
            OffsiteUploadedAt = _clock.GetUtcNow().UtcDateTime,
        };
        _db.PerTenantBackups.Add(row);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Downloaded off-site per-tenant snapshot {Bucket}/{Key} ({Bytes} bytes) for org {Slug} into local row {RowId}.",
            settings.Bucket, objectKey, size, slug, row.Id);
        return row.Id;
    }

    /// <summary>
    /// Lists objects under the configured prefix and deletes those older
    /// than <c>OffsiteRetentionDays</c>. Idempotent — safe to call on
    /// every scheduler tick.
    /// </summary>
    public async Task PruneAsync(CancellationToken ct)
    {
        var settings = await _systemSettings.ResolveOffsiteAsync(ct);
        if (settings is null) return;
        var cutoff = _clock.GetUtcNow().UtcDateTime.AddDays(-settings.RetentionDays);
        var prefix = settings.Prefix ?? string.Empty;

        var provider = _providerFactory.Create(settings);
        try
        {
            var objects = await provider.ListAsync(prefix, PruneListCap, ct);

            // Prune only this deployment's whole-DB dumps. The bucket may hold
            // neighbour deployments' files and per-tenant ZIPs (a separate
            // catalogue with its own lifecycle) — deleting those by age would
            // be cross-catalogue data loss, so reuse the same filter ListAsync
            // applies to decide what belongs to us.
            // This deployment's whole-DB dumps, newest first. Keep the most
            // recent PruneKeepRecent unconditionally so an in-flight DR snapshot
            // survives a scheduler tick (off-site has no pin concept). See #380.
            var ourDumps = objects
                .Where(o => IsWholeDbDumpKey(o.Key, prefix, out _))
                .OrderByDescending(o => o.LastModifiedUtc)
                .ToList();
            var protectedKeys = ourDumps
                .Take(PruneKeepRecent)
                .Select(o => o.Key)
                .ToHashSet(StringComparer.Ordinal);
            var stale = ourDumps
                .Where(o => o.LastModifiedUtc < cutoff && !protectedKeys.Contains(o.Key))
                .Select(o => o.Key)
                .ToList();
            if (stale.Count > 0)
            {
                await provider.DeleteAsync(stale, ct);
                _logger.LogInformation(
                    "Off-site prune deleted {Count} objects older than {Days} days from {Bucket}/{Prefix}.",
                    stale.Count, settings.RetentionDays, settings.Bucket, prefix);
            }
        }
        finally
        {
            DisposeProvider(provider);
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new Dictionary<string, string>(0);

    private static void DisposeProvider(IOffsiteStorageProvider provider)
    {
        if (provider is IDisposable disposable) disposable.Dispose();
    }

    /// <summary>
    /// Persists the post-upload row stamp; on a save failure deletes the
    /// just-uploaded object so it doesn't linger remotely with no DB record.
    /// Mirrors the download-side cleanup (<see cref="DownloadAsync"/>). The
    /// delete is best-effort — a failed delete only leaves the object for the
    /// next prune-by-age sweep — and runs on <see cref="CancellationToken.None"/>
    /// so cancellation doesn't strand the orphan. #406
    /// </summary>
    private async Task SaveOrCleanupOrphanAsync(IOffsiteStorageProvider provider, string objectKey, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            try
            {
                await provider.DeleteAsync(new[] { objectKey }, CancellationToken.None);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx,
                    "Uploaded off-site object {Key} but couldn't record it and couldn't delete the orphan; it will be reaped by prune-by-age.",
                    objectKey);
            }
            throw;
        }
    }

    private static string BuildObjectKey(string? prefix, string fileName) =>
        string.IsNullOrEmpty(prefix)
            ? fileName
            : prefix.TrimEnd('/') + "/" + fileName;

    private static string BuildPerTenantObjectKey(string? prefix, string slug, string fileName)
    {
        // Slugs are validated at org-creation time, but a stray '/' or ".."
        // here would silently write outside the tenants/<slug>/ namespace (the
        // download side already rejects such leaf names) — so fail closed
        // rather than compose a key that escapes the convention.
        if (string.IsNullOrEmpty(slug)
            || slug.Contains('/') || slug.Contains('\\') || slug.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to build an off-site key for suspicious slug: '{slug}'.");
        }
        var head = string.IsNullOrEmpty(prefix) ? string.Empty : prefix.TrimEnd('/') + "/";
        return head + PerTenantKeyPrefix + slug + "/" + fileName;
    }

    /// <summary>
    /// True when <paramref name="objectKey"/> is one of this deployment's
    /// whole-DB dumps under <paramref name="prefix"/> — i.e. it sits directly
    /// in the prefix (not the per-tenant namespace), carries the dump suffix,
    /// and has no path-traversal characters in its leaf. The off-site catalogue
    /// listing and the retention prune share this so they can't disagree about
    /// what belongs to us versus a neighbour deployment or the per-tenant ZIPs.
    /// </summary>
    private static bool IsWholeDbDumpKey(string objectKey, string prefix, out string fileName)
    {
        fileName = StripPrefix(objectKey, prefix);
        if (fileName.StartsWith(PerTenantKeyPrefix, StringComparison.Ordinal)) return false;
        if (!fileName.EndsWith(BackupService.BackupFileSuffix, StringComparison.Ordinal)) return false;
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..", StringComparison.Ordinal)) return false;
        return true;
    }

    private static string EnsureTrailingSlash(string value) =>
        value.EndsWith('/') ? value : value + "/";

    private static string StripPrefix(string objectKey, string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return objectKey;
        var normalised = prefix.EndsWith('/') ? prefix : prefix + "/";
        return objectKey.StartsWith(normalised, StringComparison.Ordinal)
            ? objectKey[normalised.Length..]
            : objectKey;
    }

    /// <summary>
    /// Streams an object body into <paramref name="stagingPath"/> (created
    /// fresh — never overwriting) in 64 KB chunks, reporting byte-level
    /// progress. Both the whole-DB and per-tenant download paths stage their
    /// bodies the same way before the atomic rename to the final path. The
    /// caller owns <paramref name="body"/> (via the <see cref="OffsiteDownload"/>
    /// it came from) and disposes it.
    /// </summary>
    private static async Task StreamToStagingAsync(
        Stream body,
        long? total,
        string stagingPath,
        IProgress<(long BytesDownloaded, long? TotalBytes)>? progress,
        CancellationToken ct)
    {
        progress?.Report((0L, total));

        long bytes = 0;
        await using var destination = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            bytes += read;
            progress?.Report((bytes, total));
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove orphaned download {Path} after a failed import.", path);
        }
    }

    /// <summary>
    /// Best-effort recovery of the original UTC timestamp from a filename
    /// shaped like <c>aldevtoolbox-yyyyMMddTHHmmssZ-(scheduled|adhoc).dump</c>.
    /// Returns <see langword="null"/> if the shape doesn't match — the
    /// caller falls back to "now" so the row still saves.
    /// </summary>
    private static DateTime? TryParseTimestamp(string fileName)
    {
        const string prefix = "aldevtoolbox-";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var rest = fileName[prefix.Length..];
        var dash = rest.IndexOf('-');
        if (dash <= 0) return null;
        var stamp = rest[..dash];
        return DateTime.TryParseExact(
            stamp,
            "yyyyMMddTHHmmssZ",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }
}
