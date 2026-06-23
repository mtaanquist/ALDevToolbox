using System.IO.Compression;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Drains <see cref="ReleaseImportQueue"/> and runs each DVD-scale import
/// (folder-ZIP upload, URL download) off the request thread, so the admin is
/// returned to the releases list immediately and watches the row flip from
/// <c>ingesting</c> to <c>ready</c> / <c>failed</c>.
///
/// <para>
/// One job at a time (the channel is single-reader): a full DVD import is
/// memory-heavy, and serialising keeps two of them from running at once. Each
/// job runs in its own DI scope under the submitting user's
/// <see cref="AmbientOrganizationScope"/> identity so EF query filters and the
/// importer's org guard behave exactly as they would in the original request.
/// </para>
/// </summary>
public sealed class ReleaseImportWorker : BackgroundService
{
    private readonly ReleaseImportQueue _queue;
    private readonly IServiceProvider _services;
    private readonly ILogger<ReleaseImportWorker> _logger;
    private readonly WorkerHeartbeat _heartbeat;

    public ReleaseImportWorker(
        ReleaseImportQueue queue,
        IServiceProvider services,
        ILogger<ReleaseImportWorker> logger,
        WorkerHeartbeatRegistry heartbeats)
    {
        _queue = queue;
        _services = services;
        _logger = logger;
        // Queue-driven: legitimately sits idle for hours waiting for an admin
        // to enqueue work, so no idle-silence ceiling (null). The active-duration
        // ceiling is the longest legitimate single import — a fresh BC base-app
        // ingest can run 30+ minutes; 90 leaves margin while still catching the
        // hung-on-I/O case that prompted this in the first place.
        _heartbeat = heartbeats.Register(nameof(ReleaseImportWorker),
            maxActiveDuration: TimeSpan.FromMinutes(90),
            maxIdleSilence: null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            _heartbeat.BeginActive();
            try
            {
                await RunJobAsync(job, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // RunJobAsync handles its own failures; this is the last-resort
                // net so one bad job never kills the worker loop.
                _logger.LogError(ex, "Release import worker tripped on ReleaseId={ReleaseId}.", job.ReleaseId);
            }
            finally
            {
                _heartbeat.EndActive();
            }
        }
    }

    private async Task RunJobAsync(ReleaseImportJob job, CancellationToken ct)
    {
        using var orgScope = AmbientOrganizationScope.Enter(job.Identity);
        await using var scope = _services.CreateAsyncScope();
        var importer = scope.ServiceProvider.GetRequiredService<ReleaseImportService>();
        var persistedJobs = scope.ServiceProvider.GetRequiredService<PersistedImportJobs>();

        // Stamp the durable row as running so the admin "Background workers"
        // page reflects current state and the startup reconciler skips this
        // row's re-enqueue on a restart that lands mid-job (the reconciler
        // resets it to queued before re-enqueuing, idempotent). JobRowId of 0
        // means a legacy in-flight job (no DB row) — skip the update.
        if (job.JobRowId != 0)
        {
            try { await persistedJobs.MarkRunningAsync(job.JobRowId, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to mark job row {JobRowId} as running.", job.JobRowId); }
        }

        var openedStreams = new List<Stream>();
        ZipArchive? archive = null;
        string? tempToDelete = null;
        // BC artifact imports stage two zips (application + platform); the
        // second slots carry the platform archive/temp file for cleanup.
        ZipArchive? archive2 = null;
        string? tempToDelete2 = null;
        var jobSucceeded = false;
        string? jobFailureMessage = null;
        try
        {
            // Legacy C/AL TXT has no .app uploads — parse the staged text file
            // directly through CalImportService and finalise via the shared
            // finally block below.
            if (job.Source is ReleaseImportSource.CalTxt calTxt)
            {
                tempToDelete = calTxt.TempPath;
                var calImporter = scope.ServiceProvider.GetRequiredService<CalImportService>();
                try
                {
                    await calImporter.ProcessReleaseAsync(job.ReleaseId, calTxt.TempPath, calTxt.EncodingName, ct).ConfigureAwait(false);
                    jobSucceeded = true;
                }
                catch (Exception ex)
                {
                    // ProcessReleaseAsync already flipped the row to failed.
                    jobFailureMessage = FriendlyMessage(ex);
                    _logger.LogError(ex, "Release {ReleaseId} C/AL import failed during processing.", job.ReleaseId);
                }
                return;
            }

            // Maintenance backfill: re-extract system references over
            // already-stored source, no upload (#291). Route to the AL or C/AL
            // path by the source flavour — C/AL stores its slices under CAL/.
            if (job.Source is ReleaseImportSource.Backfill)
            {
                try
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var isCal = await db.OeModuleFiles.AsNoTracking()
                        .Where(f => f.Module!.ReleaseId == job.ReleaseId)
                        .AnyAsync(f => f.Path.StartsWith("CAL/"), ct).ConfigureAwait(false);
                    if (isCal)
                    {
                        var calImporter = scope.ServiceProvider.GetRequiredService<CalImportService>();
                        await calImporter.BackfillSystemReferencesAsync(job.ReleaseId, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await importer.BackfillSystemReferencesAsync(job.ReleaseId, ct).ConfigureAwait(false);
                    }
                    jobSucceeded = true;
                }
                catch (Exception ex)
                {
                    jobFailureMessage = FriendlyMessage(ex);
                    _logger.LogError(ex, "Release {ReleaseId} system-reference backfill failed.", job.ReleaseId);
                }
                return;
            }

            List<AppFileUpload> uploads;
            try
            {
                switch (job.Source)
                {
                    case ReleaseImportSource.Url url:
                        var downloader = scope.ServiceProvider.GetRequiredService<DvdDownloadService>();
                        tempToDelete = await downloader.DownloadToTempAsync(url.DownloadUrl, ct).ConfigureAwait(false);
                        (uploads, archive) = ReleaseZipStaging.OpenStagedZip(tempToDelete, isDvd: true, openedStreams);
                        break;
                    case ReleaseImportSource.StagedZip staged:
                        tempToDelete = staged.TempPath;
                        (uploads, archive) = ReleaseZipStaging.OpenStagedZip(staged.TempPath, staged.IsDvd, openedStreams);
                        break;
                    case ReleaseImportSource.BcArtifact artifact:
                        var artifacts = scope.ServiceProvider.GetRequiredService<BcArtifactService>();
                        var download = await artifacts.DownloadArtifactSetAsync(artifact.ApplicationUrl, ct).ConfigureAwait(false);
                        tempToDelete = download.ApplicationZipPath;
                        tempToDelete2 = download.PlatformZipPath;
                        // Both artifact zips lay their loose .app files out under an
                        // Applications/ folder, so walk each as a DVD subset (test
                        // apps dropped) and merge. FolderZipWalker.DvdAppFolderNames
                        // already recognises "Applications".
                        (uploads, archive) = ReleaseZipStaging.OpenStagedZip(download.ApplicationZipPath, isDvd: true, openedStreams);
                        if (download.PlatformZipPath is not null)
                        {
                            var (platformUploads, platformArchive) =
                                ReleaseZipStaging.OpenStagedZip(download.PlatformZipPath, isDvd: true, openedStreams);
                            archive2 = platformArchive;
                            uploads = uploads.Concat(platformUploads).ToList();
                        }
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown import source {job.Source.GetType().Name}.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Release {ReleaseId} import failed while fetching/opening the archive.", job.ReleaseId);
                jobFailureMessage = FriendlyMessage(ex);
                await importer.MarkFailedAsync(job.ReleaseId, jobFailureMessage, ct).ConfigureAwait(false);
                return;
            }

            if (uploads.Count == 0)
            {
                var diagnostic = archive is not null
                    ? " (" + ReleaseZipStaging.DescribeAppLocations(archive) + ")"
                    : string.Empty;
                jobFailureMessage = "No application .app files were found in the archive. For a DVD we keep everything under "
                    + "its Applications (or Extensions) folder plus System.app — check the URL points to a "
                    + "Business Central DVD." + diagnostic;
                await importer.MarkFailedAsync(job.ReleaseId, jobFailureMessage, ct).ConfigureAwait(false);
                return;
            }

            // Workspace uploads: surface any app folder that declares an
            // app.json but shipped no compiled .app (not built yet). Those are
            // absent from the release by design — the importer only ingests
            // build output — but the admin should know which apps were skipped
            // so they can build and amend them in. Log-only for now.
            if (archive is not null
                && job.Source is ReleaseImportSource.StagedZip { IsDvd: false }
                && FolderZipWalker.LooksLikeWorkspace(archive))
            {
                var uncompiled = FolderZipWalker.DescribeUncompiledAppRoots(archive);
                if (uncompiled.Count > 0)
                {
                    _logger.LogWarning(
                        "Release {ReleaseId}: workspace import skipped {Count} app folder(s) with no compiled .app: {Folders}. Build them and amend the release to include them.",
                        job.ReleaseId, uncompiled.Count, string.Join(", ", uncompiled));
                }
            }

            try
            {
                await importer.ProcessReleaseAsync(job.ReleaseId, uploads, job.StoreSymbolReference, ct).ConfigureAwait(false);
                jobSucceeded = true;
            }
            catch (Exception ex)
            {
                // ProcessReleaseAsync already flips the row to failed with the
                // message; nothing to add here but a log line + the failure
                // message for the job-row update below.
                jobFailureMessage = FriendlyMessage(ex);
                _logger.LogError(ex, "Release {ReleaseId} import failed during processing.", job.ReleaseId);
            }
        }
        finally
        {
            foreach (var s in openedStreams)
            {
                try { s.Dispose(); } catch { /* swallow */ }
            }
            archive?.Dispose();
            archive2?.Dispose();
            if (tempToDelete is not null && File.Exists(tempToDelete))
            {
                try { File.Delete(tempToDelete); } catch { /* swallow */ }
            }
            if (tempToDelete2 is not null && File.Exists(tempToDelete2))
            {
                try { File.Delete(tempToDelete2); } catch { /* swallow */ }
            }
            if (job.JobRowId != 0)
            {
                try
                {
                    if (jobSucceeded)
                        await persistedJobs.MarkCompletedAsync(job.JobRowId, ct).ConfigureAwait(false);
                    else
                        await persistedJobs.MarkFailedAsync(job.JobRowId, jobFailureMessage ?? "Import failed.", ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to finalise job row {JobRowId}.", job.JobRowId);
                }
            }
        }
    }

    private static string FriendlyMessage(Exception ex) =>
        ex is PlanValidationException pve && pve.Errors.Count > 0
            ? pve.Errors.First().Value
            : ex.Message;
}
