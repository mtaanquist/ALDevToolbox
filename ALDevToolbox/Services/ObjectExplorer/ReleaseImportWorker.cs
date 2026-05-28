using System.IO.Compression;
using ALDevToolbox.Domain.ValueObjects;
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

    public ReleaseImportWorker(
        ReleaseImportQueue queue,
        IServiceProvider services,
        ILogger<ReleaseImportWorker> logger)
    {
        _queue = queue;
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
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
        }
    }

    private async Task RunJobAsync(ReleaseImportJob job, CancellationToken ct)
    {
        using var orgScope = AmbientOrganizationScope.Enter(job.Identity);
        await using var scope = _services.CreateAsyncScope();
        var importer = scope.ServiceProvider.GetRequiredService<ReleaseImportService>();

        var openedStreams = new List<Stream>();
        ZipArchive? archive = null;
        string? tempToDelete = null;
        try
        {
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
                    default:
                        throw new InvalidOperationException($"Unknown import source {job.Source.GetType().Name}.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Release {ReleaseId} import failed while fetching/opening the archive.", job.ReleaseId);
                await importer.MarkFailedAsync(job.ReleaseId, FriendlyMessage(ex), ct).ConfigureAwait(false);
                return;
            }

            if (uploads.Count == 0)
            {
                var diagnostic = archive is not null
                    ? " (" + ReleaseZipStaging.DescribeAppLocations(archive) + ")"
                    : string.Empty;
                await importer.MarkFailedAsync(
                    job.ReleaseId,
                    "No application .app files were found in the archive. For a DVD we keep everything under "
                        + "its Applications (or Extensions) folder plus System.app — check the URL points to a "
                        + "Business Central DVD." + diagnostic,
                    ct).ConfigureAwait(false);
                return;
            }

            try
            {
                await importer.ProcessReleaseAsync(job.ReleaseId, uploads, job.StoreSymbolReference, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // ProcessReleaseAsync already flips the row to failed with the
                // message; nothing to add here but a log line.
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
            if (tempToDelete is not null && File.Exists(tempToDelete))
            {
                try { File.Delete(tempToDelete); } catch { /* swallow */ }
            }
        }
    }

    private static string FriendlyMessage(Exception ex) =>
        ex is PlanValidationException pve && pve.Errors.Count > 0
            ? pve.Errors.First().Value
            : ex.Message;
}
