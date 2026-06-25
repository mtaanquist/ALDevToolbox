using System.IO.Compression;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
            DvdDownloadService dvdDownloader,
            ReleaseImportQueue queue,
            PersistedImportJobs persistedJobs,
            IOrganizationContext orgContext,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            var form = await ctx.Request.ReadFormAsync(ct);
            var label = form["Label"].ToString().Trim();
            var kind = form["Kind"].ToString().Trim();
            var publisher = form["Publisher"].ToString();
            var customerName = form["CustomerName"].ToString();
            int? parentReleaseId = null;
            if (int.TryParse(form["ParentReleaseId"].ToString(), out var pr) && pr > 0)
            {
                parentReleaseId = pr;
            }
            var metadata = new ReleaseImportMetadata(label, kind, parentReleaseId, null, publisher, customerName);
            var storeSymbolReference = form["StoreSymbolReference"].ToString() is "true" or "on";

            var dvdUrl = form["DvdUrl"].ToString().Trim();
            var folderZip = form.Files.GetFile("FolderZip");
            var appFiles = form.Files.GetFiles("AppFiles").Where(f => f.Length > 0).ToArray();
            var calTxtFile = form.Files.GetFile("CalTxtFile");
            // Legacy C/AL TXT codepage: classic finsql exports are OEM (850);
            // newer ones can be 1252. Admin-selectable, default 850.
            var calEncoding = form["CalEncoding"].ToString() is { Length: > 0 } ce ? ce : "850";

            if (dvdUrl.Length == 0 && folderZip is null && appFiles.Length == 0 && (calTxtFile is null || calTxtFile.Length == 0))
            {
                Redirect(ctx, "AppFiles", "Paste a download URL, pick a folder ZIP, pick at least one .app file, or pick a C/AL TXT export before submitting.");
                return;
            }

            try
            {
                // ── Legacy C/AL TXT: stage to disk, queue, ingest in bg ────
                if (calTxtFile is not null && calTxtFile.Length > 0)
                {
                    var releaseId = await importer.BeginReleaseAsync(metadata, ct).ConfigureAwait(false);
                    var tempPath = await TryStageAsync(ctx, importer, releaseId, calTxtFile, "oe-cal-", ".txt", "C/AL file", ct).ConfigureAwait(false);
                    if (tempPath is null) return;
                    var identity = CaptureIdentity(orgContext);
                    var source = new ReleaseImportSource.CalTxt(tempPath, calEncoding);
                    await EnqueueImportAsync(queue, persistedJobs, releaseId, identity, source, storeSymbolReference: false, ct).ConfigureAwait(false);
                    RedirectQueued(ctx, releaseId);
                    return;
                }

                // ── URL download: queue, ingest in the background ──────────
                if (dvdUrl.Length > 0)
                {
                    await dvdDownloader.ValidateUrlForQueueAsync(dvdUrl, ct).ConfigureAwait(false);
                    var releaseId = await importer.BeginReleaseAsync(metadata, ct).ConfigureAwait(false);
                    var identity = CaptureIdentity(orgContext);
                    var source = new ReleaseImportSource.Url(dvdUrl);
                    await EnqueueImportAsync(queue, persistedJobs, releaseId, identity, source, storeSymbolReference, ct).ConfigureAwait(false);
                    RedirectQueued(ctx, releaseId);
                    return;
                }

                // ── Folder-ZIP upload: stage to disk, queue, ingest in bg ──
                if (folderZip is not null && folderZip.Length > 0)
                {
                    var releaseId = await importer.BeginReleaseAsync(metadata, ct).ConfigureAwait(false);
                    var tempPath = await TryStageAsync(ctx, importer, releaseId, folderZip, "oe-folder-", ".zip", "ZIP", ct).ConfigureAwait(false);
                    if (tempPath is null) return;
                    var identity = CaptureIdentity(orgContext);
                    var source = new ReleaseImportSource.StagedZip(tempPath, IsDvd: false);
                    await EnqueueImportAsync(queue, persistedJobs, releaseId, identity, source, storeSymbolReference, ct).ConfigureAwait(false);
                    RedirectQueued(ctx, releaseId);
                    return;
                }

                // ── Individual files: small/fast, stays synchronous ────────
                var openedStreams = new List<Stream>();
                try
                {
                    var uploads = BuildUploadsFromIndividualFiles(form, appFiles, openedStreams);
                    var request = new ReleaseImportRequest(
                        Label: label,
                        Kind: kind,
                        ParentReleaseId: parentReleaseId,
                        ApplicationVersionId: null,
                        Uploads: uploads,
                        Publisher: publisher,
                        CustomerName: customerName,
                        StoreSymbolReference: storeSymbolReference);

                    var summary = await importer.ImportReleaseAsync(request, ct);

                    var query = $"/object-explorer/release/{summary.ReleaseId}"
                        + $"?ok=imported"
                        + $"&modules={summary.ModulesImported}"
                        + $"&skipped={summary.ModulesSkipped}"
                        + $"&refs={summary.ReferencesImported}"
                        + $"&translations={summary.TranslationsImported}";
                    ctx.Response.Redirect(query);
                }
                finally
                {
                    foreach (var s in openedStreams)
                    {
                        try { s.Dispose(); } catch { /* swallow */ }
                    }
                }
            }
            catch (PlanValidationException ex)
            {
                // URL/allow-list validation, label collisions, quota — all
                // field-keyed so the form renders them inline. Keep a C/AL
                // submission on the C/AL tab (its label/parent errors would
                // otherwise surface on Upload, which no longer has the field).
                var first = ex.Errors.First();
                var page = calTxtFile is { Length: > 0 } ? "/admin/object-explorer/new/cal" : null;
                Redirect(ctx, first.Key, first.Value, page);
            }
        })
        .RequireObjectExplorerAuthoring()
        .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes))
        .WithMetadata(new RequestFormLimitsAttribute
        {
            MultipartBodyLengthLimit = MaxUploadBytes,
            MultipartHeadersLengthLimit = 32 * 1024,
        });

        // ── Amend modules into an existing release (#216) ───────────────
        // Mirrors /admin/object-explorer/import but binds to an existing
        // release id; reuses the same FolderZip / AppFiles + SourceZips
        // form shape so the upload-building helpers come for free.
        // Same 1 GB cap — a late-landing partner DVD is rare but possible.
        app.MapPost("/admin/object-explorer/release/{releaseId:int}/modules", async (
            int releaseId,
            HttpContext ctx,
            ReleaseImportService importer,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            var form = await ctx.Request.ReadFormAsync(ct);
            var folderZip = form.Files.GetFile("FolderZip");
            var appFiles = form.Files.GetFiles("AppFiles").Where(f => f.Length > 0).ToArray();

            if (folderZip is null && appFiles.Length == 0)
            {
                RedirectAmend(ctx, releaseId, "AppFiles", "Pick a folder ZIP or at least one .app file before submitting.");
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

                ReleaseImportSummary summary;
                try
                {
                    summary = await importer.AmendReleaseAsync(releaseId, uploads, ct);
                }
                catch (PlanValidationException ex)
                {
                    var first = ex.Errors.First();
                    RedirectAmend(ctx, releaseId, first.Key, first.Value);
                    return;
                }

                ctx.Response.Redirect(
                    $"/admin/object-explorer/release/{releaseId}/modules"
                    + $"?ok=amended"
                    + $"&modules={summary.ModulesImported}"
                    + $"&skipped={summary.ModulesSkipped}"
                    + $"&refs={summary.ReferencesImported}");
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
        .RequireObjectExplorerAuthoring()
        .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes))
        .WithMetadata(new RequestFormLimitsAttribute
        {
            MultipartBodyLengthLimit = MaxUploadBytes,
            MultipartHeadersLengthLimit = 32 * 1024,
        });

        // The lightweight read-side /api/object-explorer/* GET endpoints the
        // static source viewer hits (go-to-definition, find-in-file, download,
        // SymbolReference stream, outline dependencies, references sessions)
        // live in a sibling file but register here, so Program.cs's single
        // MapObjectExplorerEndpoints() call still wires everything up. They're
        // RequireAuthorization() (any signed-in org user reads), unlike the
        // Admin,Editor authoring POSTs below.
        app.MapObjectExplorerViewerEndpoints();

        // ── Translation uploads (#151) ─────────────────────────────────
        // Two admin POSTs: single .xlf against one module, or per-release
        // ZIP holding many .xlf files matched to modules by the XLIFF's
        // <file original> attribute. Both clobber existing rows for the
        // affected (module, language) pairs so re-upload is the recovery
        // story when a translation needs updating. 64 MB cap — a single
        // .xlf is well under 5 MB and a 12-language ZIP comfortably fits.
        app.MapPost("/admin/object-explorer/release/{releaseId:int}/modules/{moduleId:long}/translations", async (
            int releaseId,
            long moduleId,
            HttpContext ctx,
            TranslationImportService translations,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("XliffFile");
            if (file is null || file.Length == 0)
            {
                RedirectTranslations(ctx, releaseId, "XliffFile", "Pick an .xlf file before submitting.");
                return;
            }
            try
            {
                await using var stream = file.OpenReadStream();
                var summary = await translations.ImportSingleAsync(releaseId, moduleId, stream, file.FileName, ct);
                ctx.Response.Redirect(
                    $"/admin/object-explorer/release/{releaseId}/translations"
                    + $"?ok=imported&lang={Uri.EscapeDataString(summary.LanguageCode)}"
                    + $"&module={Uri.EscapeDataString(summary.ModuleName)}"
                    + $"&count={summary.Inserted}");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                RedirectTranslations(ctx, releaseId, first.Key, first.Value);
            }
        })
        .RequireObjectExplorerAuthoring()
        .WithMetadata(new RequestSizeLimitAttribute(64L * 1024 * 1024));

        app.MapPost("/admin/object-explorer/release/{releaseId:int}/translations-zip", async (
            int releaseId,
            HttpContext ctx,
            TranslationImportService translations,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("ZipFile");
            if (file is null || file.Length == 0)
            {
                RedirectTranslations(ctx, releaseId, "ZipFile", "Pick a ZIP file holding one or more .xlf files before submitting.");
                return;
            }
            try
            {
                await using var stream = file.OpenReadStream();
                var summary = await translations.ImportZipAsync(releaseId, stream, ct);
                ctx.Response.Redirect(
                    $"/admin/object-explorer/release/{releaseId}/translations"
                    + $"?ok=zip-imported&matched={summary.MatchedFiles}"
                    + $"&skipped={summary.UnmatchedFiles.Count}"
                    + $"&inserted={summary.TotalInserted}");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                RedirectTranslations(ctx, releaseId, first.Key, first.Value);
            }
        })
        .RequireObjectExplorerAuthoring()
        .WithMetadata(new RequestSizeLimitAttribute(64L * 1024 * 1024));

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
                ctx.Response.Redirect($"/admin/object-explorer/release/{id}/manage?ok=soft-deleted");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                RedirectManage(ctx, id, first.Key, first.Value);
            }
        }).RequireObjectExplorerAuthoring();

        // Retry a failed import in place — re-runs into the SAME release row
        // (label / metadata preserved) instead of forcing a delete-and-reimport.
        // A URL import re-runs from its original (or a freshly pasted) URL with
        // no re-upload; a staged-ZIP / C-AL import needs the file re-uploaded
        // because its temp file is gone. Either way we wipe the previous
        // attempt's partial data so the re-run starts clean. See the manage page.
        app.MapPost("/admin/object-explorer/{id:int}/retry", async (
            int id,
            HttpContext ctx,
            ReleaseImportService importer,
            ReleaseManagementService management,
            DvdDownloadService dvdDownloader,
            ReleaseImportQueue queue,
            PersistedImportJobs persistedJobs,
            IOrganizationContext orgContext,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            var form = await ctx.Request.ReadFormAsync(ct);
            var dvdUrl = form["DvdUrl"].ToString().Trim();
            var folderZip = form.Files.GetFile("FolderZip");
            var calTxtFile = form.Files.GetFile("CalTxtFile");
            var calEncoding = form["CalEncoding"].ToString() is { Length: > 0 } ce ? ce : "850";
            var storeSymbolReference = form["StoreSymbolReference"].ToString() is "true" or "on";

            var origin = await persistedJobs.GetLatestForReleaseAsync(id, ct);

            // Customer builds re-run from the customer id alone — no URL, no
            // re-upload. Reopen the release, wipe the previous attempt's modules,
            // and re-enqueue a CustomerBuild job (the build service replaces the
            // per-app report). Handled before the URL/upload validation below.
            if (origin is { Kind: "customer_build", CustomerId: int retryCustomerId })
            {
                try
                {
                    // ReopenForRebuildAsync (not ReopenForRetryAsync) so a partial
                    // build — which lands `ready`, not `failed` — can be plainly
                    // re-run without a symbol upload when its failure was
                    // transient. See issue #433.
                    await importer.ReopenForRebuildAsync(id, ct).ConfigureAwait(false);
                    await management.ClearIngestedDataAsync(id, ct).ConfigureAwait(false);
                    var identity = CaptureIdentity(orgContext);
                    var source = new ReleaseImportSource.CustomerBuild(retryCustomerId);
                    await EnqueueImportAsync(queue, persistedJobs, id, identity, source, storeSymbolReference: false, ct).ConfigureAwait(false);
                    ctx.Response.Redirect($"/admin/object-explorer/release/{id}/manage?ok=retry-queued");
                }
                catch (PlanValidationException ex)
                {
                    var first = ex.Errors.First();
                    RedirectManage(ctx, id, first.Key, first.Value);
                }
                return;
            }

            try
            {
                var hasFolderZip = folderZip is not null && folderZip.Length > 0;
                var hasCalTxt = calTxtFile is not null && calTxtFile.Length > 0;

                // Resolve the URL to use (pasted wins; else reuse the original
                // URL import) and validate it against the allow-list BEFORE we
                // touch the release, so a bad URL leaves the failed row untouched.
                string? urlToUse = null;
                if (dvdUrl.Length > 0)
                {
                    urlToUse = dvdUrl;
                }
                else if (!hasFolderZip && !hasCalTxt)
                {
                    if (origin is { Kind: "url", DownloadUrl: { Length: > 0 } originalUrl })
                    {
                        urlToUse = originalUrl;
                    }
                    else
                    {
                        throw RetryValidation(
                            "There's nothing to re-run automatically — the original upload isn't on disk any more. "
                            + "Paste a download URL, or re-upload the ZIP / C-AL file, to retry.");
                    }
                }
                if (urlToUse is not null)
                {
                    await dvdDownloader.ValidateUrlForQueueAsync(urlToUse, ct).ConfigureAwait(false);
                }

                // Flip failed → ingesting (validates state) and wipe the previous
                // attempt's partial modules so the re-run can't skip a
                // half-written module on the idempotency check.
                await importer.ReopenForRetryAsync(id, ct).ConfigureAwait(false);
                await management.ClearIngestedDataAsync(id, ct).ConfigureAwait(false);

                ReleaseImportSource source;
                if (urlToUse is not null)
                {
                    source = new ReleaseImportSource.Url(urlToUse);
                }
                else if (hasFolderZip)
                {
                    var tempPath = await TryStageAsync(ctx, importer, id, folderZip!, "oe-folder-", ".zip", "ZIP", ct).ConfigureAwait(false);
                    if (tempPath is null) return;
                    // A URL-origin DVD re-uploaded as a zip is still a DVD subset;
                    // otherwise honour the original staged flag (defaults to the
                    // whole-archive / workspace walk).
                    var isDvd = origin?.Kind == "url" || (origin?.StagedIsDvd ?? false);
                    source = new ReleaseImportSource.StagedZip(tempPath, isDvd);
                }
                else
                {
                    var tempPath = await TryStageAsync(ctx, importer, id, calTxtFile!, "oe-cal-", ".txt", "C/AL file", ct).ConfigureAwait(false);
                    if (tempPath is null) return;
                    source = new ReleaseImportSource.CalTxt(tempPath, calEncoding);
                }

                var identity = CaptureIdentity(orgContext);
                await EnqueueImportAsync(queue, persistedJobs, id, identity, source, storeSymbolReference, ct).ConfigureAwait(false);

                ctx.Response.Redirect($"/admin/object-explorer/release/{id}/manage?ok=retry-queued");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                RedirectManage(ctx, id, first.Key, first.Value);
            }
        })
        .RequireObjectExplorerAuthoring()
        .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes))
        .WithMetadata(new RequestFormLimitsAttribute
        {
            MultipartBodyLengthLimit = MaxUploadBytes,
            MultipartHeadersLengthLimit = 32 * 1024,
        });

        // Manual-symbols recovery: upload the dependency .app(s) a customer build
        // couldn't resolve, store them against the customer, and rebuild this same
        // release. Works on a partial (ready) build as well as a fully failed one —
        // the typical case is one extension that failed for a missing third-party
        // symbol while its siblings ingested. See
        // .design/object-explorer-customer-builds.md ("Manual-symbols recovery").
        app.MapPost("/admin/object-explorer/{id:int}/recover-symbols", async (
            int id,
            HttpContext ctx,
            CustomerService customers,
            ReleaseImportService importer,
            ReleaseManagementService management,
            ReleaseImportQueue queue,
            PersistedImportJobs persistedJobs,
            IOrganizationContext orgContext,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            var origin = await persistedJobs.GetLatestForReleaseAsync(id, ct);
            if (origin is not { Kind: "customer_build", CustomerId: int customerId })
            {
                RedirectManage(ctx, id, "Symbols", "This release isn't a customer build, so there's nothing to recover.");
                return;
            }

            try
            {
                var form = await ctx.Request.ReadFormAsync(ct);
                var files = form.Files.GetFiles("Symbols").Where(f => f.Length > 0).ToList();
                if (files.Count == 0)
                {
                    throw new PlanValidationException(new Dictionary<string, string>
                    {
                        ["Symbols"] = "Choose at least one .app symbol package to upload.",
                    });
                }

                var uploads = new List<SupplementalSymbolUpload>(files.Count);
                foreach (var file in files)
                {
                    using var buffer = new MemoryStream();
                    await using (var stream = file.OpenReadStream())
                    {
                        await stream.CopyToAsync(buffer, ct);
                    }
                    uploads.Add(new SupplementalSymbolUpload(SanitiseFileName(file.FileName), buffer.ToArray()));
                }

                // Persist the symbols first so they survive even if the rebuild
                // can't be queued, and so every later build of this customer
                // benefits. Then rebuild this release in place.
                await customers.AddSupplementalSymbolsAsync(customerId, uploads, ct);

                await importer.ReopenForRebuildAsync(id, ct);
                await management.ClearIngestedDataAsync(id, ct);
                var identity = CaptureIdentity(orgContext);
                var source = new ReleaseImportSource.CustomerBuild(customerId);
                await EnqueueImportAsync(queue, persistedJobs, id, identity, source, storeSymbolReference: false, ct);

                ctx.Response.Redirect($"/admin/object-explorer/release/{id}/manage?ok=recover-queued");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                RedirectManage(ctx, id, first.Key, first.Value);
            }
        })
        .RequireObjectExplorerAuthoring()
        .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes))
        .WithMetadata(new RequestFormLimitsAttribute
        {
            MultipartBodyLengthLimit = MaxUploadBytes,
            MultipartHeadersLengthLimit = 32 * 1024,
        });

        // Maintenance: re-extract system references over already-stored source
        // for one release (no re-upload) — backfills oe_module_system_references
        // for releases imported before #279. Queued like an import; processed by
        // ReleaseImportWorker, which routes AL vs C/AL. See #291.
        app.MapPost("/admin/object-explorer/{id:int}/backfill-system-references", async (
            int id,
            HttpContext ctx,
            ReleaseImportQueue queue,
            PersistedImportJobs persistedJobs,
            IOrganizationContext orgContext,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var identity = CaptureIdentity(orgContext);
            var source = new ReleaseImportSource.Backfill();
            await EnqueueImportAsync(queue, persistedJobs, id, identity, source, storeSymbolReference: false, ct);
            ctx.Response.Redirect($"/admin/object-explorer/release/{id}/manage?ok=backfill-queued");
        }).RequireObjectExplorerAuthoring();

        // Bulk variant: enqueue a backfill for every ready, non-deleted release
        // in the org — the "I don't want to reimport my whole catalogue" path.
        app.MapPost("/admin/object-explorer/backfill-system-references-all", async (
            HttpContext ctx,
            AppDbContext db,
            ReleaseImportQueue queue,
            PersistedImportJobs persistedJobs,
            IOrganizationContext orgContext,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var identity = CaptureIdentity(orgContext);
            // Query-filtered to the caller's org; no cross-tenant enqueue.
            var releaseIds = await db.OeReleases.AsNoTracking()
                .Where(r => r.Status == "ready" && r.DeletedAt == null)
                .Select(r => r.Id)
                .ToListAsync(ct);
            foreach (var rid in releaseIds)
            {
                var source = new ReleaseImportSource.Backfill();
                await EnqueueImportAsync(queue, persistedJobs, rid, identity, source, storeSymbolReference: false, ct);
            }
            ctx.Response.Redirect($"/admin/object-explorer?ok=backfill-all-queued&id={releaseIds.Count}");
        }).RequireObjectExplorerAuthoring();

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
                ctx.Response.Redirect($"/admin/object-explorer/release/{id}/manage?ok=restored");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                RedirectManage(ctx, id, first.Key, first.Value);
            }
        }).RequireObjectExplorerAuthoring();

        app.MapPost("/admin/object-explorer/{id:int}/metadata", async (
            int id,
            HttpContext ctx,
            ReleaseManagementService management,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var publisher = form["Publisher"].ToString();
            var customerName = form["CustomerName"].ToString();

            try
            {
                await management.UpdateMetadataAsync(id, publisher, customerName, ct);
                ctx.Response.Redirect($"/admin/object-explorer/release/{id}/manage?ok=updated");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                RedirectManage(ctx, id, first.Key, first.Value);
            }
        }).RequireObjectExplorerAuthoring();

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
                // The release is gone, so there's no manage page to return to.
                ctx.Response.Redirect("/admin/object-explorer?ok=hard-deleted&id=" + id);
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                RedirectManage(ctx, id, first.Key, first.Value);
            }
        }).RequireObjectExplorerAuthoring();

        return app;
    }

    /// <summary>
    /// Authorisation shared by every mutating Object Explorer admin endpoint:
    /// the same <c>Admin,Editor</c> set the OE admin pages declare
    /// (<c>[Authorize(Roles = "Admin,Editor")]</c>). Object Explorer is a
    /// content-authoring surface, so Editors operate it fully — see CLAUDE.md's
    /// role model. Centralised here so the endpoint policy can't silently drift
    /// from the page policy again: when they disagreed, an Editor's POST 403'd
    /// and the cookie handler's AccessDeniedPath bounced them to /login, which
    /// looked like being logged out mid-upload.
    /// </summary>
    private static RouteHandlerBuilder RequireObjectExplorerAuthoring(this RouteHandlerBuilder builder) =>
        builder.RequireAuthorization(policy => policy.RequireRole(
            HttpOrganizationContext.AdminRole, HttpOrganizationContext.EditorRole));

    private static void RedirectManage(HttpContext ctx, int releaseId, string errKey, string message)
    {
        ctx.Response.Redirect(
            $"/admin/object-explorer/release/{releaseId}/manage?err=" + Uri.EscapeDataString(errKey)
            + "&msg=" + Uri.EscapeDataString(message));
    }

    private static void RedirectTranslations(HttpContext ctx, int releaseId, string errKey, string message)
    {
        ctx.Response.Redirect(
            $"/admin/object-explorer/release/{releaseId}/translations?err=" + Uri.EscapeDataString(errKey)
            + "&msg=" + Uri.EscapeDataString(message));
    }

    /// <summary>Field-keyed (<c>Retry</c>) validation error for the retry endpoint's inline messages.</summary>
    private static PlanValidationException RetryValidation(string message) =>
        new(new Dictionary<string, string> { ["Retry"] = message });

    private static void RedirectAmend(HttpContext ctx, int releaseId, string errKey, string message)
    {
        ctx.Response.Redirect(
            $"/admin/object-explorer/release/{releaseId}/modules?err=" + Uri.EscapeDataString(errKey)
            + "&msg=" + Uri.EscapeDataString(message));
    }

    // ── Folder-ZIP path ────────────────────────────────────────────────

    private static AmbientOrganizationScope.OrganizationIdentity CaptureIdentity(IOrganizationContext orgContext) =>
        new(
            OrganizationId: orgContext.CurrentOrganizationId
                ?? throw new InvalidOperationException("No organization in scope when queuing a release import."),
            UserId: orgContext.CurrentUserId,
            IsSiteAdmin: orgContext.IsSiteAdmin,
            IsSystemOrganization: orgContext.IsSystemOrganization);

    private static void RedirectQueued(HttpContext ctx, int releaseId) =>
        ctx.Response.Redirect($"/admin/object-explorer?ok=queued&id={releaseId}");

    /// <summary>
    /// The shared two-step enqueue every import path performs: persist a
    /// <c>queued</c> job row, then push the matching in-memory
    /// <see cref="ReleaseImportJob"/> stamped with that row id onto the
    /// channel the background worker drains. Each caller keeps its own redirect
    /// because the success target differs (release page vs manage page vs none
    /// for the bulk loop).
    /// </summary>
    private static async Task EnqueueImportAsync(
        ReleaseImportQueue queue,
        PersistedImportJobs persistedJobs,
        int releaseId,
        AmbientOrganizationScope.OrganizationIdentity identity,
        ReleaseImportSource source,
        bool storeSymbolReference,
        CancellationToken ct)
    {
        var jobRowId = await persistedJobs.CreateAsync(releaseId, identity, source, storeSymbolReference, ct).ConfigureAwait(false);
        await queue.EnqueueAsync(
            new ReleaseImportJob(releaseId, identity, source, storeSymbolReference, jobRowId),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stages an uploaded file to a temp path the background worker reopens,
    /// folding the out-of-scratch-disk failure handling every caller needs:
    /// the release row already exists, so an <see cref="IOException"/> records
    /// the failure on it and sends the admin to the queued list rather than a
    /// 500. Returns the temp path, or <c>null</c> after performing the
    /// failure-redirect — in which case the caller must <c>return</c>.
    /// </summary>
    private static async Task<string?> TryStageAsync(
        HttpContext ctx,
        ReleaseImportService importer,
        int releaseId,
        Microsoft.AspNetCore.Http.IFormFile file,
        string prefix,
        string extension,
        string failedNoun,
        CancellationToken ct)
    {
        try
        {
            return await StageUploadToTempAsync(file, prefix, extension, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            await importer.MarkFailedAsync(releaseId, $"Could not stage the uploaded {failedNoun} to disk: " + ex.Message, ct).ConfigureAwait(false);
            RedirectQueued(ctx, releaseId);
            return null;
        }
    }

    /// <summary>
    /// Streams an uploaded folder ZIP to a temp file (ZipArchive needs a
    /// seekable stream, and the background worker reopens it after the request
    /// ends). Returns the temp path; the worker deletes it when done.
    /// </summary>
    private static async Task<string> StageFolderZipToTempAsync(
        Microsoft.AspNetCore.Http.IFormFile folderZip, CancellationToken ct)
        => await StageUploadToTempAsync(folderZip, "oe-folder-", ".zip", ct).ConfigureAwait(false);

    /// <summary>
    /// Streams an uploaded file to a temp file the background worker reopens
    /// after the request ends (the worker deletes it when done). Used for both
    /// the folder ZIP and the raw C/AL TXT — neither fits through the Blazor
    /// circuit at 150 MB+.
    /// </summary>
    private static async Task<string> StageUploadToTempAsync(
        Microsoft.AspNetCore.Http.IFormFile file, string prefix, string extension, CancellationToken ct)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N") + extension);
        await using var fs = File.Create(tempPath);
        await using var src = file.OpenReadStream();
        await src.CopyToAsync(fs, ct).ConfigureAwait(false);
        return tempPath;
    }

    /// <summary>
    /// Stages the uploaded folder ZIP to a temp file and walks every
    /// <c>.app</c> entry — the synchronous amend path (#216) consumes the entry
    /// streams in-request, so it gets the archive + temp path back to dispose.
    /// </summary>
    private static async Task<(List<AppFileUpload> Uploads, ZipArchive Archive, string TempPath)>
        BuildUploadsFromFolderZipAsync(
            Microsoft.AspNetCore.Http.IFormFile folderZip,
            List<Stream> openedStreams,
            CancellationToken ct)
    {
        var tempPath = await StageFolderZipToTempAsync(folderZip, ct).ConfigureAwait(false);
        var (uploads, archive) = ReleaseZipStaging.OpenStagedZip(tempPath, isDvd: false, openedStreams);
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

    private static void Redirect(HttpContext ctx, string errKey, string message, string? page = null)
    {
        // An explicit page wins (the caller knows which tab the post came from).
        // Otherwise DVD-URL errors come from the DVD tab; everything else is the
        // Upload tab's folder-ZIP picker.
        page ??= errKey == "DvdUrl" ? "/admin/object-explorer/new/dvd" : "/admin/object-explorer/new";
        ctx.Response.Redirect(
            page + "?err=" + Uri.EscapeDataString(errKey)
            + "&msg=" + Uri.EscapeDataString(message));
    }
}
