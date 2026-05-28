using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// CRUD over the durable <c>oe_import_jobs</c> rows that mirror every
/// <see cref="ReleaseImportQueue"/> enqueue. Created so the in-memory channel
/// can survive a process restart: the row carries everything the worker needs
/// to resume a URL download (the only source kind we can pick back up — staged
/// uploads live in container-local <c>/tmp</c> and are gone after restart).
///
/// <para>
/// Scoped lifetime: takes <see cref="AppDbContext"/>, no shared mutable state.
/// The endpoint creates a row, the worker updates its status, the startup
/// reconciler scans for survivors. The reconciler runs cross-org by design,
/// matching the same blessed startup-maintenance bucket the migration / seed
/// / interrupted-release sweep already occupies in <c>StartupTasks</c>.
/// </para>
/// </summary>
public sealed class PersistedImportJobs
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public PersistedImportJobs(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Writes a new <c>queued</c> row for a release the endpoint just created.
    /// Returns the generated row id so the caller can stamp it on the
    /// <see cref="ReleaseImportJob"/> it enqueues to the in-memory channel.
    /// </summary>
    public async Task<long> CreateAsync(
        int releaseId,
        AmbientOrganizationScope.OrganizationIdentity identity,
        ReleaseImportSource source,
        bool storeSymbolReference,
        CancellationToken ct = default)
    {
        var row = new ImportJob
        {
            OrganizationId = identity.OrganizationId,
            ReleaseId = releaseId,
            UserId = identity.UserId,
            IsSiteAdmin = identity.IsSiteAdmin,
            IsSystemOrganization = identity.IsSystemOrganization,
            StoreSymbolReference = storeSymbolReference,
            Status = "queued",
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        switch (source)
        {
            case ReleaseImportSource.Url url:
                row.Kind = "url";
                row.DownloadUrl = url.DownloadUrl;
                break;
            case ReleaseImportSource.StagedZip staged:
                row.Kind = "staged_zip";
                row.StagedZipPath = staged.TempPath;
                row.StagedIsDvd = staged.IsDvd;
                break;
            default:
                throw new InvalidOperationException($"Unknown import source {source.GetType().Name}.");
        }
        _db.OeImportJobs.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row.Id;
    }

    public async Task MarkRunningAsync(long jobRowId, CancellationToken ct = default)
    {
        var row = await _db.OeImportJobs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(j => j.Id == jobRowId, ct).ConfigureAwait(false);
        if (row is null) return;
        row.Status = "running";
        row.StartedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkCompletedAsync(long jobRowId, CancellationToken ct = default)
    {
        var row = await _db.OeImportJobs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(j => j.Id == jobRowId, ct).ConfigureAwait(false);
        if (row is null) return;
        row.Status = "completed";
        row.CompletedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(long jobRowId, string errorMessage, CancellationToken ct = default)
    {
        var row = await _db.OeImportJobs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(j => j.Id == jobRowId, ct).ConfigureAwait(false);
        if (row is null) return;
        row.Status = "failed";
        row.ErrorMessage = Truncate(errorMessage, 4000);
        row.CompletedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Startup reconciliation. Returns the URL-source jobs to re-enqueue; the
    /// caller (StartupTasks) pushes them back through <see cref="ReleaseImportQueue.EnqueueAsync"/>.
    /// Staged-zip jobs are flipped to <c>failed</c> in-place (their temp file
    /// is gone) along with their owning <c>oe_releases</c> row so the admin sees
    /// the failure reason on the list and can re-submit.
    /// </summary>
    public async Task<IReadOnlyList<ReleaseImportJob>> ReconcileOnStartupAsync(CancellationToken ct = default)
    {
        // Cross-org sweep — same blessed startup-maintenance bucket the
        // migration / seed / interrupted-release reconciliation already
        // sits in (see StartupTasks). No user in scope at startup; reading
        // every org's job rows here is the design.
        var survivors = await _db.OeImportJobs.IgnoreQueryFilters()
            .Where(j => j.Status == "queued" || j.Status == "running")
            .ToListAsync(ct).ConfigureAwait(false);
        if (survivors.Count == 0) return Array.Empty<ReleaseImportJob>();

        var now = _clock.GetUtcNow().UtcDateTime;
        var toResume = new List<ReleaseImportJob>();
        var lostReleaseIds = new List<int>();

        foreach (var row in survivors)
        {
            switch (row.Kind)
            {
                case "url" when !string.IsNullOrEmpty(row.DownloadUrl):
                    // Reset to queued so the worker re-runs from scratch. The
                    // download is idempotent: it writes to a fresh temp file
                    // and the importer creates a single transaction per
                    // release.
                    row.Status = "queued";
                    row.StartedAt = null;
                    toResume.Add(new ReleaseImportJob(
                        ReleaseId: row.ReleaseId,
                        Identity: new AmbientOrganizationScope.OrganizationIdentity(
                            row.OrganizationId, row.UserId, row.IsSiteAdmin, row.IsSystemOrganization),
                        Source: new ReleaseImportSource.Url(row.DownloadUrl),
                        StoreSymbolReference: row.StoreSymbolReference,
                        JobRowId: row.Id));
                    break;
                default:
                    // Staged-zip can't be resumed — the temp file is gone. The
                    // existing OeRelease row reconciler in StartupTasks already
                    // flips ingesting releases to failed, but its message is
                    // generic; record a more specific failure on the job row.
                    row.Status = "failed";
                    row.ErrorMessage = "The uploaded ZIP was lost when the container restarted. Re-submit to try again.";
                    row.CompletedAt = now;
                    lostReleaseIds.Add(row.ReleaseId);
                    break;
            }
        }

        // For lost staged-zip jobs, flip the matching OeRelease row to failed
        // with the same message so the list-page surfaces the cause.
        if (lostReleaseIds.Count > 0)
        {
            var releases = await _db.OeReleases.IgnoreQueryFilters()
                .Where(r => lostReleaseIds.Contains(r.Id) && r.Status == "ingesting")
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var r in releases)
            {
                r.Status = "failed";
                r.StatusMessage = "The uploaded ZIP was lost when the container restarted. Re-submit to try again.";
                r.UpdatedAt = now;
            }
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return toResume;
    }

    /// <summary>Snapshot for the admin "Background workers" page — depth + recent rows.</summary>
    public async Task<ImportQueueSnapshot> SnapshotAsync(int recentLimit = 10, CancellationToken ct = default)
    {
        var pending = await _db.OeImportJobs.IgnoreQueryFilters()
            .CountAsync(j => j.Status == "queued" || j.Status == "running", ct).ConfigureAwait(false);
        var recent = await _db.OeImportJobs.IgnoreQueryFilters()
            .OrderByDescending(j => j.CreatedAt)
            .Take(recentLimit)
            .Select(j => new ImportQueueRow(
                j.Id, j.ReleaseId, j.Kind, j.Status, j.CreatedAt, j.StartedAt, j.CompletedAt, j.ErrorMessage))
            .ToListAsync(ct).ConfigureAwait(false);
        return new ImportQueueSnapshot(pending, recent);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max));
}

public sealed record ImportQueueSnapshot(int Pending, IReadOnlyList<ImportQueueRow> Recent);

public sealed record ImportQueueRow(
    long Id,
    int ReleaseId,
    string Kind,
    string Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? ErrorMessage);
