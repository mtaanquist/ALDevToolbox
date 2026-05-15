using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Al;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Background service that backfills the Object Explorer symbol index for
/// versions imported before the symbol feature existed (or any version
/// whose <see cref="BaseAppVersion.SymbolsIndexedAt"/> got cleared by an
/// extractor change).
///
/// New imports already populate symbols inline inside
/// <see cref="BaseAppImportService.ImportAsync"/>; this scheduler exists
/// solely so users who upgraded mid-flight get their existing versions
/// reindexed without re-uploading the ZIPs. Polls every five minutes,
/// processes one version per tick, and stops chewing on the database once
/// there's nothing left to do (the tick is a no-op when every version is
/// already indexed).
/// </summary>
public sealed class SymbolReindexer : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private const int BatchSize = 200;

    private readonly IServiceProvider _services;
    private readonly SymbolReindexQueue _queue;
    private readonly ILogger<SymbolReindexer> _logger;

    public SymbolReindexer(
        IServiceProvider services, SymbolReindexQueue queue, ILogger<SymbolReindexer> logger)
    {
        _services = services;
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give startup migrations and seed a head start.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SymbolReindexer tick threw; will retry on the next poll.");
            }

            // Wait for either a wakeup signal (e.g. admin clicked "Reindex
            // now") or the poll timeout, whichever fires first. Using a
            // linked CTS so the timer disposes cleanly when a wakeup arrives.
            using var timer = new CancellationTokenSource(PollInterval);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, timer.Token);
            await _queue.WaitAsync(linked.Token);
            if (stoppingToken.IsCancellationRequested) return;
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await TickOnceAsync(db, ct);
    }

    /// <summary>
    /// One scheduler decision against the supplied context. Exposed
    /// internally so tests can drive it directly without the
    /// <see cref="Task.Delay"/> loop.
    /// </summary>
    internal async Task TickOnceAsync(AppDbContext db, CancellationToken ct)
    {
        // Pre-login work: query filters scope reads by organisation, and
        // the reindexer runs without an HTTP context. IgnoreQueryFilters
        // lets us see every org's data — the rows we then insert respect
        // the version's own organisation_id so cross-tenant isolation stays
        // intact downstream.
        var pending = await db.BaseAppVersions
            .IgnoreQueryFilters()
            .Where(v => v.DeletedAt == null && v.SymbolsIndexedAt == null && v.FileCount > 0)
            .OrderBy(v => v.UploadedAt)
            .Select(v => v.Id)
            .FirstOrDefaultAsync(ct);

        if (pending == 0) return;

        await ReindexVersionAsync(db, pending, ct);
    }

    /// <summary>
    /// Replaces all symbol rows for the given version with a fresh
    /// extraction over the stored file content. Wrapped in a transaction
    /// so a partial failure doesn't leave half-indexed symbols.
    /// </summary>
    public async Task<int> ReindexVersionAsync(AppDbContext db, int versionId, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var version = await db.BaseAppVersions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(v => v.Id == versionId, ct);
        if (version is null) return 0;

        var previousAutoDetect = db.ChangeTracker.AutoDetectChangesEnabled;
        var totalSymbols = 0;

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // Wipe any existing symbol rows for this version. ExecuteDelete
            // bypasses the change tracker — fine because there's nothing
            // tracked here yet.
            await db.BaseAppSymbols
                .IgnoreQueryFilters()
                .Where(s => s.VersionId == versionId)
                .ExecuteDeleteAsync(ct);

            db.ChangeTracker.AutoDetectChangesEnabled = false;

            // Stream file ids + content in chunks. Loading every file into
            // memory at once would blow out the heap on a real Base
            // Application; pagination by id keeps it flat.
            long lastId = 0;
            while (!ct.IsCancellationRequested)
            {
                var chunk = await db.BaseAppFiles
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(f => f.VersionId == versionId && f.Id > lastId)
                    .OrderBy(f => f.Id)
                    .Take(BatchSize)
                    .Select(f => new { f.Id, f.OrganizationId, f.Content })
                    .ToListAsync(ct);
                if (chunk.Count == 0) break;

                var batch = new List<BaseAppSymbol>(BatchSize * 4);
                foreach (var file in chunk)
                {
                    foreach (var symbol in AlSymbolExtractor.Extract(file.Content))
                    {
                        batch.Add(new BaseAppSymbol
                        {
                            OrganizationId = file.OrganizationId,
                            VersionId = versionId,
                            FileId = file.Id,
                            Kind = symbol.Kind,
                            Name = symbol.Name,
                            Signature = symbol.Signature,
                            FieldId = symbol.FieldId,
                            LineNumber = symbol.LineNumber,
                            ColumnStart = symbol.ColumnStart,
                            ColumnEnd = symbol.ColumnEnd,
                        });
                    }
                }

                if (batch.Count > 0)
                {
                    db.BaseAppSymbols.AddRange(batch);
                    await db.SaveChangesAsync(ct);
                    db.ChangeTracker.Clear();
                    totalSymbols += batch.Count;
                }

                lastId = chunk[^1].Id;
            }

            version.SymbolsIndexedAt = DateTime.UtcNow;
            db.BaseAppVersions.Update(version);
            await db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = previousAutoDetect;
            db.ChangeTracker.Clear();
        }

        var duration = DateTime.UtcNow - startedAt;
        _logger.LogInformation(
            "Reindexed Object Explorer symbols for version {VersionId}: {SymbolCount} symbol(s) in {Seconds:F1}s.",
            versionId, totalSymbols, duration.TotalSeconds);

        return totalSymbols;
    }
}
