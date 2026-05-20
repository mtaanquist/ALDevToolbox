using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Result of <see cref="OffsiteBackupService.TestConnectionAsync"/>. The
/// settings form renders the message inline so SiteAdmins can verify the
/// credentials before flipping the schedule on.
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
/// Uploads full-database pg_dump backups to the configured S3-compatible
/// bucket and prunes objects older than <c>OffsiteRetentionDays</c>. Only
/// the whole-DB backups produced by <see cref="BackupService"/> go off-site
/// — per-tenant logical snapshots stay on the local <c>app-backups</c>
/// volume because they're the in-cluster "restore my tenant to yesterday"
/// surface; the off-site copy is disaster recovery for the whole deployment.
/// </summary>
public sealed class OffsiteBackupService
{
    private readonly AppDbContext _db;
    private readonly SystemSettingsService _systemSettings;
    private readonly BackupService _backups;
    private readonly ILogger<OffsiteBackupService> _logger;
    private readonly TimeProvider _clock;

    public OffsiteBackupService(
        AppDbContext db,
        SystemSettingsService systemSettings,
        BackupService backups,
        ILogger<OffsiteBackupService> logger,
        TimeProvider clock)
    {
        _db = db;
        _systemSettings = systemSettings;
        _backups = backups;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>HEADs the configured bucket as a connection test. Returns a structured result for the UI to render inline.</summary>
    public async Task<OffsiteTestResult> TestConnectionAsync(CancellationToken ct)
    {
        var settings = await _systemSettings.ResolveOffsiteAsync(ct);
        if (settings is null)
        {
            return new OffsiteTestResult(false, "Off-site backup is not fully configured. Save the form with credentials and a bucket first.");
        }
        try
        {
            using var client = CreateClient(settings);
            await client.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = settings.Bucket }, ct);
            return new OffsiteTestResult(true, $"Connected to bucket '{settings.Bucket}'.");
        }
        catch (AmazonS3Exception ex)
        {
            return new OffsiteTestResult(false, $"S3 error: {ex.ErrorCode} — {ex.Message}");
        }
        catch (Exception ex)
        {
            return new OffsiteTestResult(false, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Uploads a single backup row's file to the configured bucket and
    /// stamps the row with <c>OffsiteUploadedAt</c> + <c>OffsiteObjectKey</c>.
    /// No-op when off-site is disabled or the file is missing locally.
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
        using var client = CreateClient(settings);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = settings.Bucket,
            Key = objectKey,
            InputStream = stream,
            AutoCloseStream = false,
            ContentType = "application/octet-stream",
            DisablePayloadSigning = settings.ForcePathStyle,
        }, ct);

        row.OffsiteUploadedAt = _clock.GetUtcNow().UtcDateTime;
        row.OffsiteObjectKey = objectKey;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Uploaded backup {FileName} to s3://{Bucket}/{Key}.",
            row.FileName, settings.Bucket, objectKey);
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
    /// a giant bucket — a small cap keeps the page fast and the S3 bill
    /// predictable.</param>
    public async Task<IReadOnlyList<OffsiteObjectInfo>> ListAsync(int maxObjects, CancellationToken ct)
    {
        var settings = await _systemSettings.ResolveOffsiteAsync(ct);
        if (settings is null) return Array.Empty<OffsiteObjectInfo>();
        if (maxObjects < 1) maxObjects = 1;

        var prefix = settings.Prefix ?? string.Empty;
        var results = new List<OffsiteObjectInfo>(capacity: Math.Min(maxObjects, 256));
        using var client = CreateClient(settings);
        string? continuation = null;
        do
        {
            var resp = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = settings.Bucket,
                Prefix = prefix,
                ContinuationToken = continuation,
                MaxKeys = Math.Min(1000, maxObjects - results.Count),
            }, ct);

            foreach (var obj in resp.S3Objects)
            {
                var fileName = StripPrefix(obj.Key, prefix);
                // Skip anything that doesn't look like one of our dumps —
                // the bucket may have other content (logs, neighbour
                // deployments) and we don't want to offer those as
                // restorable.
                if (!fileName.EndsWith(BackupService.BackupFileSuffix, StringComparison.Ordinal)) continue;
                if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..", StringComparison.Ordinal)) continue;
                results.Add(new OffsiteObjectInfo(obj.Key, fileName, obj.Size, obj.LastModified.ToUniversalTime()));
                if (results.Count >= maxObjects) break;
            }

            continuation = resp.IsTruncated == true && results.Count < maxObjects
                ? resp.NextContinuationToken
                : null;
        } while (continuation is not null);

        results.Sort((a, b) => b.LastModified.CompareTo(a.LastModified));
        return results;
    }

    /// <summary>
    /// Downloads a single object from the bucket into the local backups
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

        using var client = CreateClient(settings);
        using var response = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = settings.Bucket,
            Key = objectKey,
        }, ct);

        var total = response.ContentLength > 0 ? response.ContentLength : (long?)null;
        progress?.Report((0L, total));

        long bytes = 0;
        await using (var source = response.ResponseStream)
        await using (var destination = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                bytes += read;
                progress?.Report((bytes, total));
            }
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
            // that the operator just hauled down from S3. They can unpin
            // after the restore.
            IsPinned = true,
            OffsiteObjectKey = objectKey,
            OffsiteUploadedAt = _clock.GetUtcNow().UtcDateTime,
        };
        _db.Backups.Add(row);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Downloaded off-site backup s3://{Bucket}/{Key} ({Bytes} bytes) into local row {BackupId}.",
            settings.Bucket, objectKey, size, row.Id);
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

        using var client = CreateClient(settings);
        string? continuation = null;
        do
        {
            var resp = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = settings.Bucket,
                Prefix = settings.Prefix,
                ContinuationToken = continuation,
            }, ct);

            var stale = resp.S3Objects.Where(o => o.LastModified.ToUniversalTime() < cutoff).ToList();
            if (stale.Count > 0)
            {
                await client.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = settings.Bucket,
                    Objects = stale.Select(o => new KeyVersion { Key = o.Key }).ToList(),
                    Quiet = true,
                }, ct);
                _logger.LogInformation(
                    "Off-site prune deleted {Count} objects older than {Days} days from s3://{Bucket}/{Prefix}.",
                    stale.Count, settings.RetentionDays, settings.Bucket, settings.Prefix ?? string.Empty);
            }

            continuation = resp.IsTruncated == true ? resp.NextContinuationToken : null;
        } while (continuation is not null);
    }

    private static IAmazonS3 CreateClient(ResolvedOffsiteSettings settings)
    {
        var credentials = new BasicAWSCredentials(settings.AccessKey, settings.SecretKey);
        var config = new AmazonS3Config
        {
            ForcePathStyle = settings.ForcePathStyle,
        };
        if (!string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            config.ServiceURL = settings.Endpoint;
        }
        else if (!string.IsNullOrWhiteSpace(settings.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);
        }
        return new AmazonS3Client(credentials, config);
    }

    private static string BuildObjectKey(string? prefix, string fileName) =>
        string.IsNullOrEmpty(prefix)
            ? fileName
            : prefix.TrimEnd('/') + "/" + fileName;

    private static string StripPrefix(string objectKey, string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return objectKey;
        var normalised = prefix.EndsWith('/') ? prefix : prefix + "/";
        return objectKey.StartsWith(normalised, StringComparison.Ordinal)
            ? objectKey[normalised.Length..]
            : objectKey;
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
