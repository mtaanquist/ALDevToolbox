using System.IO.Compression;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// HTTP endpoints for the Object Explorer surface (Releases / Modules /
/// Find references). The bulk-upload endpoint lives here rather than on a
/// Blazor InteractiveServer page so a 1 GB DVD body can stream through
/// Kestrel instead of buffering through the SignalR circuit.
///
/// The form accepts either of two upload shapes:
/// <list type="bullet">
///   <item><c>FolderZip</c> — a single ZIP wrapping the DVD's
///         <c>applications/</c> folder tree. Walked server-side; each
///         <c>.app</c> entry is paired with its sibling <c>.Source.zip</c>
///         in the same directory and flag inference (test / internal /
///         language-pack) follows the DVD's folder conventions.</item>
///   <item><c>AppFiles</c> + <c>SourceZips</c> — individual file pickers,
///         useful for partner extensions you've built locally without the
///         DVD layout.</item>
/// </list>
/// </summary>
internal static class ObjectExplorerEndpoints
{
    /// <summary>
    /// 1 GB cap on the multipart upload. BC 28.1's <c>applications/</c>
    /// folder zipped lands around 700 MB, so this gives a comfortable
    /// margin for the largest first-party DVD.
    /// </summary>
    public const long MaxUploadBytes = 1024L * 1024 * 1024;

    public static IEndpointRouteBuilder MapObjectExplorerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/object-explorer/import", async (
            HttpContext ctx,
            ReleaseImportService importer,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            var form = await ctx.Request.ReadFormAsync(ct);
            var label = form["Label"].ToString().Trim();
            var kind = form["Kind"].ToString().Trim();
            int? parentReleaseId = null;
            if (int.TryParse(form["ParentReleaseId"].ToString(), out var pr) && pr > 0)
            {
                parentReleaseId = pr;
            }

            var folderZip = form.Files.GetFile("FolderZip");
            var appFiles = form.Files.GetFiles("AppFiles").Where(f => f.Length > 0).ToArray();

            if (folderZip is null && appFiles.Length == 0)
            {
                Redirect(ctx, "AppFiles", "Pick a folder ZIP or at least one .app file before submitting.");
                return;
            }

            var openedStreams = new List<Stream>();
            ZipArchive? folderArchive = null;
            string? tempFolderZipPath = null;
            try
            {
                List<AppFileUpload> uploads;
                if (folderZip is not null && folderZip.Length > 0)
                {
                    (uploads, folderArchive, tempFolderZipPath) =
                        await BuildUploadsFromFolderZipAsync(folderZip, openedStreams, ct).ConfigureAwait(false);
                }
                else
                {
                    uploads = BuildUploadsFromIndividualFiles(form, appFiles, openedStreams);
                }

                var request = new ReleaseImportRequest(
                    Label: label,
                    Kind: kind,
                    ParentReleaseId: parentReleaseId,
                    ApplicationVersionId: null,
                    Uploads: uploads);

                ReleaseImportSummary summary;
                try
                {
                    summary = await importer.ImportReleaseAsync(request, ct);
                }
                catch (PlanValidationException ex)
                {
                    var first = ex.Errors.First();
                    Redirect(ctx, first.Key, first.Value);
                    return;
                }

                var query = $"/object-explorer/release/{summary.ReleaseId}"
                    + $"?ok=imported"
                    + $"&modules={summary.ModulesImported}"
                    + $"&skipped={summary.ModulesSkipped}"
                    + $"&refs={summary.ReferencesImported}";
                ctx.Response.Redirect(query);
            }
            finally
            {
                foreach (var s in openedStreams)
                {
                    try { s.Dispose(); } catch { /* swallow */ }
                }
                folderArchive?.Dispose();
                if (tempFolderZipPath is not null && File.Exists(tempFolderZipPath))
                {
                    try { File.Delete(tempFolderZipPath); } catch { /* swallow */ }
                }
            }
        })
        .RequireAuthorization(policy => policy.RequireRole("Admin"))
        .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes))
        .WithMetadata(new RequestFormLimitsAttribute
        {
            MultipartBodyLengthLimit = MaxUploadBytes,
            MultipartHeadersLengthLimit = 32 * 1024,
        });

        return app;
    }

    // ── Folder-ZIP path ────────────────────────────────────────────────

    /// <summary>
    /// Stages the uploaded folder ZIP to a temp file (ZipArchive needs a
    /// seekable stream) and walks every <c>.app</c> entry. Returns the
    /// staged-archive handle and temp path so the caller can dispose them
    /// after the import service finishes consuming the entry streams.
    /// </summary>
    private static async Task<(List<AppFileUpload> Uploads, ZipArchive Archive, string TempPath)>
        BuildUploadsFromFolderZipAsync(
            Microsoft.AspNetCore.Http.IFormFile folderZip,
            List<Stream> openedStreams,
            CancellationToken ct)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "oe-folder-" + Guid.NewGuid().ToString("N") + ".zip");
        await using (var fs = File.Create(tempPath))
        await using (var src = folderZip.OpenReadStream())
        {
            await src.CopyToAsync(fs, ct).ConfigureAwait(false);
        }

        var archive = new ZipArchive(File.OpenRead(tempPath), ZipArchiveMode.Read);
        var entries = FolderZipWalker.Walk(archive);

        var uploads = new List<AppFileUpload>(entries.Count);
        foreach (var entry in entries)
        {
            var appStream = entry.AppEntry.Open();
            openedStreams.Add(appStream);

            Stream? sourceStream = null;
            if (entry.SourceZipEntry is not null)
            {
                sourceStream = entry.SourceZipEntry.Open();
                openedStreams.Add(sourceStream);
            }

            uploads.Add(new AppFileUpload(
                FileName: entry.FileName,
                AppStream: appStream,
                SourceZipStream: sourceStream,
                IsTest: entry.IsTest,
                IsInternal: entry.IsInternal,
                IsLanguagePack: entry.IsLanguagePack));
        }
        return (uploads, archive, tempPath);
    }

    // ── Individual-file path (legacy / partner extensions) ─────────────

    private static List<AppFileUpload> BuildUploadsFromIndividualFiles(
        Microsoft.AspNetCore.Http.IFormCollection form,
        IReadOnlyList<Microsoft.AspNetCore.Http.IFormFile> appFiles,
        List<Stream> openedStreams)
    {
        var sourceZips = form.Files.GetFiles("SourceZips").Where(f => f.Length > 0).ToArray();
        var sourceByBasename = sourceZips.ToDictionary(
            f => Path.GetFileNameWithoutExtension(f.FileName).Replace(".Source", "", StringComparison.OrdinalIgnoreCase),
            f => f,
            StringComparer.OrdinalIgnoreCase);

        var uploads = new List<AppFileUpload>(appFiles.Count);
        foreach (var af in appFiles)
        {
            var appStream = af.OpenReadStream();
            openedStreams.Add(appStream);
            Stream? sourceStream = null;
            var stem = Path.GetFileNameWithoutExtension(af.FileName);
            foreach (var key in EnumeratePossibleSourceKeys(stem))
            {
                if (sourceByBasename.TryGetValue(key, out var match))
                {
                    sourceStream = match.OpenReadStream();
                    openedStreams.Add(sourceStream);
                    break;
                }
            }
            uploads.Add(new AppFileUpload(af.FileName, appStream, sourceStream));
        }
        return uploads;
    }

    /// <summary>
    /// Tries a few reasonable name shapes for "what .Source.zip pairs with
    /// this .app" on the individual-file path. The folder-ZIP path has its
    /// own pairing logic (<see cref="FolderZipWalker"/>) that takes
    /// containing-directory + stem into account; this fallback handles the
    /// flat-folder partner case.
    /// </summary>
    private static IEnumerable<string> EnumeratePossibleSourceKeys(string stem)
    {
        yield return stem;

        var underscore = stem.IndexOf('_');
        if (underscore > 0)
        {
            var trimmed = stem[(underscore + 1)..];
            yield return trimmed;
            yield return trimmed.Replace('_', ' ');
        }

        yield return stem.Replace('_', ' ');
    }

    private static void Redirect(HttpContext ctx, string errKey, string message)
    {
        ctx.Response.Redirect(
            "/admin/object-explorer/new?err=" + Uri.EscapeDataString(errKey)
            + "&msg=" + Uri.EscapeDataString(message));
    }
}
