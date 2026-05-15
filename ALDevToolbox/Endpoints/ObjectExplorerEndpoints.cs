using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// HTTP endpoints for the Object Explorer surface (Releases / Modules /
/// Find references). The bulk-upload endpoint lives here rather than on a
/// Blazor InteractiveServer page so a 100-app DVD body can stream through
/// Kestrel instead of buffering through the SignalR circuit.
/// </summary>
internal static class ObjectExplorerEndpoints
{
    /// <summary>500 MB cap on the multipart upload — generous for a full BC DVD.</summary>
    public const long MaxUploadBytes = 500L * 1024 * 1024;

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

            var appFiles = form.Files.GetFiles("AppFiles").Where(f => f.Length > 0).ToArray();
            if (appFiles.Length == 0)
            {
                Redirect(ctx, "AppFiles", "Pick at least one .app file before submitting.");
                return;
            }

            // Map each AppFile to its paired SourceZip (by file basename match
            // on the "<AppName>.Source.zip" / "<AppName>.app" convention). Any
            // unpaired SourceZips are dropped silently — the .app's own
            // embedded source covers most cases.
            var sourceZips = form.Files.GetFiles("SourceZips").Where(f => f.Length > 0).ToArray();
            var sourceByBasename = sourceZips.ToDictionary(
                f => Path.GetFileNameWithoutExtension(f.FileName).Replace(".Source", "", StringComparison.OrdinalIgnoreCase),
                f => f,
                StringComparer.OrdinalIgnoreCase);

            var uploads = new List<AppFileUpload>(appFiles.Length);
            var openedStreams = new List<Stream>();
            try
            {
                foreach (var af in appFiles)
                {
                    var appStream = af.OpenReadStream();
                    openedStreams.Add(appStream);
                    Stream? sourceStream = null;
                    // Convention: Microsoft_DK_Core.app + DK Core.Source.zip,
                    // or whatever shape the admin used. We look up by the
                    // dotted-bits in the middle.
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

    /// <summary>
    /// Tries a few reasonable name shapes for "what .Source.zip pairs with
    /// this .app". Returns: the bare stem, the stem with a leading publisher
    /// prefix stripped (<c>Microsoft_DK_Core → DK_Core → DK Core</c>), and
    /// the stem with underscores replaced by spaces. Order matters — most
    /// specific to least.
    /// </summary>
    private static IEnumerable<string> EnumeratePossibleSourceKeys(string stem)
    {
        yield return stem;

        // Strip a "Microsoft_" / "<Publisher>_" prefix when present.
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
        // Error redirects go back to the form page (/new); the POST endpoint
        // (/import) is action-only and has no GET view.
        ctx.Response.Redirect(
            "/admin/object-explorer/new?err=" + Uri.EscapeDataString(errKey)
            + "&msg=" + Uri.EscapeDataString(message));
    }
}
