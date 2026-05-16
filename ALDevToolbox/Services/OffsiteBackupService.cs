using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Result of <see cref="OffsiteBackupService.TestConnectionAsync"/>. The
/// settings form renders the message inline so SiteAdmins can verify the
/// credentials before flipping the schedule on.
/// </summary>
public sealed record OffsiteTestResult(bool Success, string Message);

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
}
