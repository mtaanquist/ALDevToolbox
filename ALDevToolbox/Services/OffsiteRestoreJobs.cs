using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ALDevToolbox.Services;

/// <summary>
/// Lifecycle of an off-site restore download job. The SiteAdmin page
/// renders the active rows as a progress bar; terminal rows (Completed,
/// Failed) linger in the dictionary for an hour so a redirected operator
/// can still see what happened.
/// </summary>
public enum OffsiteRestoreJobStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
}

/// <summary>
/// Discriminator between whole-DB dump downloads and per-tenant snapshot
/// downloads. The worker uses this to pick the right
/// <see cref="OffsiteBackupService"/> method; the status endpoint and the
/// SiteAdmin pages use it to decide which catalogue surfaces the row.
/// </summary>
public enum OffsiteRestoreJobKind
{
    WholeDb = 0,
    PerTenant = 1,
}

/// <summary>
/// Immutable snapshot of an in-flight or terminal off-site restore job.
/// The <see cref="OffsiteRestoreJobs"/> singleton swaps the value in the
/// dictionary on every progress tick rather than mutating it in place so
/// concurrent JSON-status reads always see a consistent view.
/// </summary>
public sealed record OffsiteRestoreJob(
    Guid Id,
    OffsiteRestoreJobKind Kind,
    string ObjectKey,
    string FileName,
    DateTime StartedAt,
    DateTime UpdatedAt,
    long BytesDownloaded,
    long? TotalBytes,
    OffsiteRestoreJobStatus Status,
    string? Error,
    int? BackupId);

/// <summary>
/// Process-local job tracker for off-site restore downloads. Holds the
/// state every active and recently-terminal job and the queue the
/// background worker drains. Singleton because there's only ever one
/// process-wide worker; the JSON status endpoint and the worker share
/// this instance.
///
/// <para>
/// Job state lives in memory. A process restart drops in-flight jobs —
/// acceptable for a feature that's nearly always going to be used at
/// most once per deployment lifetime. The staging-file rename in
/// <see cref="OffsiteBackupService.DownloadAsync"/> ensures a crash
/// mid-download doesn't leave a corrupt local row behind.
/// </para>
/// </summary>
public sealed class OffsiteRestoreJobs
{
    private static readonly TimeSpan RetainTerminalFor = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, OffsiteRestoreJob> _jobs = new();
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly TimeProvider _clock;

    public OffsiteRestoreJobs(TimeProvider clock)
    {
        _clock = clock;
    }

    internal ChannelReader<Guid> Reader => _queue.Reader;

    /// <summary>
    /// Registers a new job and enqueues it for the worker. Returns the
    /// freshly-minted id so the endpoint can redirect to a page that
    /// knows which job to poll.
    /// </summary>
    public Guid Enqueue(OffsiteRestoreJobKind kind, string objectKey, string fileName)
    {
        EvictOldTerminal();
        var now = _clock.GetUtcNow().UtcDateTime;
        var id = Guid.NewGuid();
        var job = new OffsiteRestoreJob(
            Id: id,
            Kind: kind,
            ObjectKey: objectKey,
            FileName: fileName,
            StartedAt: now,
            UpdatedAt: now,
            BytesDownloaded: 0,
            TotalBytes: null,
            Status: OffsiteRestoreJobStatus.Pending,
            Error: null,
            BackupId: null);
        _jobs[id] = job;
        if (!_queue.Writer.TryWrite(id))
        {
            // Unbounded channel never refuses a write under normal
            // conditions; the only path here is a closed writer, which we
            // never invoke. Surface it loudly if it ever does happen.
            throw new InvalidOperationException("Off-site restore queue is closed.");
        }
        return id;
    }

    public OffsiteRestoreJob? Get(Guid id) => _jobs.TryGetValue(id, out var job) ? job : null;

    /// <summary>Snapshot of every non-evicted job (active + recently terminal), newest first.</summary>
    public IReadOnlyList<OffsiteRestoreJob> List()
    {
        EvictOldTerminal();
        return _jobs.Values
            .OrderByDescending(j => j.StartedAt)
            .ToList();
    }

    internal void MarkRunning(Guid id, long? totalBytes)
    {
        Update(id, j => j with
        {
            Status = OffsiteRestoreJobStatus.Running,
            TotalBytes = totalBytes,
            UpdatedAt = _clock.GetUtcNow().UtcDateTime,
        });
    }

    internal void ReportProgress(Guid id, long bytesDownloaded, long? totalBytes)
    {
        Update(id, j => j with
        {
            BytesDownloaded = bytesDownloaded,
            TotalBytes = totalBytes ?? j.TotalBytes,
            UpdatedAt = _clock.GetUtcNow().UtcDateTime,
        });
    }

    internal void MarkCompleted(Guid id, int backupId)
    {
        Update(id, j => j with
        {
            Status = OffsiteRestoreJobStatus.Completed,
            BackupId = backupId,
            UpdatedAt = _clock.GetUtcNow().UtcDateTime,
        });
    }

    internal void MarkFailed(Guid id, string error)
    {
        Update(id, j => j with
        {
            Status = OffsiteRestoreJobStatus.Failed,
            Error = error,
            UpdatedAt = _clock.GetUtcNow().UtcDateTime,
        });
    }

    private void Update(Guid id, Func<OffsiteRestoreJob, OffsiteRestoreJob> mutate)
    {
        // AddOrUpdate's update factory races with concurrent updates;
        // ConcurrentDictionary handles that — the worker is the only
        // mutator anyway, but the loop guard keeps it correct if that
        // changes.
        while (_jobs.TryGetValue(id, out var current))
        {
            var next = mutate(current);
            if (_jobs.TryUpdate(id, next, current)) return;
        }
    }

    private void EvictOldTerminal()
    {
        var cutoff = _clock.GetUtcNow().UtcDateTime - RetainTerminalFor;
        foreach (var (id, job) in _jobs)
        {
            if (job.Status is OffsiteRestoreJobStatus.Completed or OffsiteRestoreJobStatus.Failed
                && job.UpdatedAt < cutoff)
            {
                _jobs.TryRemove(id, out _);
            }
        }
    }
}

/// <summary>
/// Drains the <see cref="OffsiteRestoreJobs"/> queue one job at a time
/// and runs each download in a scoped <see cref="OffsiteBackupService"/>.
/// Sequential by design — concurrent multi-gigabyte downloads to the
/// same volume would just thrash disk.
/// </summary>
public sealed class OffsiteRestoreWorker : BackgroundService
{
    private readonly OffsiteRestoreJobs _jobs;
    private readonly IServiceProvider _services;
    private readonly ILogger<OffsiteRestoreWorker> _logger;

    public OffsiteRestoreWorker(
        OffsiteRestoreJobs jobs,
        IServiceProvider services,
        ILogger<OffsiteRestoreWorker> logger)
    {
        _jobs = jobs;
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in _jobs.Reader.ReadAllAsync(stoppingToken))
        {
            await RunOneAsync(jobId, stoppingToken);
        }
    }

    private async Task RunOneAsync(Guid jobId, CancellationToken stoppingToken)
    {
        var job = _jobs.Get(jobId);
        if (job is null) return;

        await using var scope = _services.CreateAsyncScope();
        var offsite = scope.ServiceProvider.GetRequiredService<OffsiteBackupService>();

        try
        {
            _jobs.MarkRunning(jobId, totalBytes: null);
            var progress = new Progress<(long BytesDownloaded, long? TotalBytes)>(tuple =>
                _jobs.ReportProgress(jobId, tuple.BytesDownloaded, tuple.TotalBytes));
            var backupId = job.Kind switch
            {
                OffsiteRestoreJobKind.WholeDb => await offsite.DownloadAsync(job.ObjectKey, progress, stoppingToken),
                OffsiteRestoreJobKind.PerTenant => await offsite.DownloadPerTenantAsync(job.ObjectKey, progress, stoppingToken),
                _ => throw new InvalidOperationException($"Unknown off-site restore job kind: {job.Kind}"),
            };
            _jobs.MarkCompleted(jobId, backupId);
            _logger.LogInformation(
                "Off-site restore job {JobId} ({Kind}) for {ObjectKey} completed as local row {RowId}.",
                jobId, job.Kind, job.ObjectKey, backupId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _jobs.MarkFailed(jobId, "Cancelled by host shutdown.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Off-site restore job {JobId} for {ObjectKey} failed.", jobId, job.ObjectKey);
            _jobs.MarkFailed(jobId, ex.Message);
        }
    }
}
