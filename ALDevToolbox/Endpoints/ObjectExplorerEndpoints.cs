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

        // ── JSON read-side endpoints for the static-SSR source viewer ──
        // Replace the Blazor-callback path used by the legacy interactive
        // viewer. The static page's source-viewer.js hits these on Cmd/Ctrl
        // click and right-click "Find in this file" so no SignalR circuit
        // is required for in-document navigation. See
        // .design/source-viewer-redesign.md.
        app.MapGet("/api/object-explorer/files/{fileId:long}/goto", async (
            long fileId,
            int line,
            int column,
            ObjectExplorerService oe,
            CancellationToken ct) =>
        {
            var target = await oe.GoToDefinitionAsync(fileId, line, column, ct);
            return target is null ? Results.NoContent() : Results.Ok(target);
        }).RequireAuthorization();

        app.MapGet("/api/object-explorer/files/{fileId:long}/find-in-file", async (
            long fileId,
            int line,
            int column,
            ObjectExplorerService oe,
            CancellationToken ct) =>
        {
            var result = await oe.FindInFileAsync(fileId, line, column, ct);
            return result is null ? Results.NoContent() : Results.Ok(result);
        }).RequireAuthorization();

        // Download the raw source for a file. Streams the stored content with
        // a Content-Disposition: attachment header so the browser saves it
        // under the file's path basename rather than rendering it inline.
        app.MapGet("/api/object-explorer/files/{fileId:long}/download", async (
            long fileId,
            ObjectExplorerService oe,
            CancellationToken ct) =>
        {
            var file = await oe.GetFileAsync(fileId, ct);
            if (file is null) return Results.NotFound();
            var fileName = file.Path;
            var slash = fileName.LastIndexOf('/');
            if (slash >= 0) fileName = fileName[(slash + 1)..];
            if (string.IsNullOrEmpty(fileName)) fileName = $"file-{fileId}.al";
            var bytes = System.Text.Encoding.UTF8.GetBytes(file.Content ?? string.Empty);
            return Results.File(bytes, "text/plain; charset=utf-8", fileName);
        }).RequireAuthorization();

        // Outline dependencies (#148): the file viewer's outline lazy-loads
        // "Using" and "Used by" sections after the static-SSR paint. One
        // round-trip per page load.
        app.MapGet("/api/object-explorer/files/{fileId:long}/dependencies", async (
            long fileId,
            ObjectExplorerService oe,
            CancellationToken ct) =>
        {
            var result = await oe.GetFileDependenciesAsync(fileId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireAuthorization();

        // ── References-session endpoints ──────────────────────────────
        // Persist a Find-references search across file navigations.
        // The token returned here travels in the URL as ?refSet=…, and
        // the source viewer page reads it server-side to render the
        // persistent References panel. See ReferenceSessionService.
        // GET (rather than POST) because the action is idempotent for a
        // given symbol id + user: the cache key is per-user, and re-minting
        // returns a fresh token without altering DB state. No antiforgery
        // headache for a fetch() call from source-viewer.js.
        app.MapGet("/api/object-explorer/references/sessions/from-symbol/{symbolId:long}", async (
            long symbolId,
            HttpContext ctx,
            ReferenceSessionService sessions,
            CancellationToken ct) =>
        {
            var owner = OwnerKey(ctx);
            if (owner is null) return Results.Unauthorized();
            var session = await sessions.CreateFromSymbolAsync(symbolId, owner, ct);
            return session is null ? Results.NotFound() : Results.Ok(session);
        }).RequireAuthorization();

        // Member-scoped find: outline right-click on a procedure / field /
        // trigger row mints a session that bundles declarations + indirect
        // owner-type refs + (eventually) method-call refs. The symbolId
        // here is an oe_module_symbols row, not an object id.
        app.MapGet("/api/object-explorer/references/sessions/from-member-symbol/{symbolId:long}", async (
            long symbolId,
            HttpContext ctx,
            ReferenceSessionService sessions,
            CancellationToken ct) =>
        {
            var owner = OwnerKey(ctx);
            if (owner is null) return Results.Unauthorized();
            var session = await sessions.CreateFromMemberSymbolAsync(symbolId, owner, ct);
            return session is null ? Results.NotFound() : Results.Ok(session);
        }).RequireAuthorization();

        app.MapGet("/api/object-explorer/references/sessions/{token}", (
            string token,
            HttpContext ctx,
            ReferenceSessionService sessions) =>
        {
            var owner = OwnerKey(ctx);
            if (owner is null) return Results.Unauthorized();
            var session = sessions.Get(token, owner);
            return session is null ? Results.NotFound() : Results.Ok(session);
        }).RequireAuthorization();

        // Right-click "Find references" on a non-declaration token: the
        // server inspects the word at (line, column) and resolves it to a
        // same-Release object before minting the session.
        app.MapGet("/api/object-explorer/references/sessions/at-position", async (
            long fileId,
            int line,
            int column,
            HttpContext ctx,
            ReferenceSessionService sessions,
            CancellationToken ct) =>
        {
            var owner = OwnerKey(ctx);
            if (owner is null) return Results.Unauthorized();
            var session = await sessions.CreateAtPositionAsync(fileId, line, column, owner, ct);
            return session is null ? Results.NoContent() : Results.Ok(session);
        }).RequireAuthorization();

        app.MapPost("/admin/object-explorer/{id:int}/soft-delete", async (
            int id,
            HttpContext ctx,
            ReleaseManagementService management,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            try
            {
                await management.SoftDeleteAsync(id, ct);
                ctx.Response.Redirect("/admin/object-explorer?ok=soft-deleted&id=" + id);
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                RedirectAdmin(ctx, first.Key, first.Value);
            }
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        app.MapPost("/admin/object-explorer/{id:int}/restore", async (
            int id,
            HttpContext ctx,
            ReleaseManagementService management,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            try
            {
                await management.RestoreAsync(id, ct);
                ctx.Response.Redirect("/admin/object-explorer?ok=restored&id=" + id);
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                RedirectAdmin(ctx, first.Key, first.Value);
            }
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        app.MapPost("/admin/object-explorer/{id:int}/hard-delete", async (
            int id,
            HttpContext ctx,
            ReleaseManagementService management,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var confirm = form["ConfirmLabel"].ToString();

            try
            {
                await management.HardDeleteAsync(id, confirm, ct);
                ctx.Response.Redirect("/admin/object-explorer?ok=hard-deleted&id=" + id);
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                RedirectAdmin(ctx, first.Key, first.Value);
            }
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        return app;
    }

    private static void RedirectAdmin(HttpContext ctx, string errKey, string message)
    {
        ctx.Response.Redirect(
            "/admin/object-explorer?err=" + Uri.EscapeDataString(errKey)
            + "&msg=" + Uri.EscapeDataString(message));
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
