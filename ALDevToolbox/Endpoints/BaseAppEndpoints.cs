using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// HTTP endpoints for the Object Explorer admin pages. The ZIP upload lives
/// here (not inside a Blazor page) because Blazor's <c>InputFile</c> pipes
/// uploads through the SignalR circuit chunk-by-chunk — fine for a 1 MB TOML
/// but painful for the 100–200 MB Base Application archives. A plain
/// multipart POST handled here streams the body straight into the import
/// service without going through interop.
/// </summary>
internal static class BaseAppEndpoints
{
    /// <summary>500 MB cap on the multipart upload. Covers a generous Base Application ZIP with room to spare.</summary>
    public const long MaxUploadBytes = 500L * 1024 * 1024;

    public static IEndpointRouteBuilder MapBaseAppEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/object-explorer/import", async (
            HttpContext ctx,
            BaseAppImportService importer,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            var form = await ctx.Request.ReadFormAsync(ct);
            var zipFiles = form.Files.GetFiles("Zip").Where(f => f.Length > 0).ToArray();
            if (zipFiles.Length == 0)
            {
                Redirect(ctx, "Zip", "Choose at least one ZIP file before submitting.");
                return;
            }

            if (!int.TryParse(form["Major"], out var major)
                || !int.TryParse(form["CumulativeUpdate"], out var cu))
            {
                Redirect(ctx, "Version", "Major and CumulativeUpdate must be integers.");
                return;
            }

            int? applicationVersionId = null;
            var appVerRaw = form["ApplicationVersionId"].ToString();
            if (!string.IsNullOrEmpty(appVerRaw) && int.TryParse(appVerRaw, out var appVer))
            {
                applicationVersionId = appVer;
            }

            var modeRaw = form["Mode"].ToString();
            if (!Enum.TryParse<BaseAppImportMode>(modeRaw, ignoreCase: true, out var firstMode))
            {
                firstMode = BaseAppImportMode.Reject;
            }

            var notes = form["Notes"].ToString();
            var totalParsed = 0;
            var totalFailed = 0;
            var totalReplaced = 0;
            int? versionId = null;
            string? firstError = null;
            string? firstErrorKey = null;

            for (var i = 0; i < zipFiles.Length; i++)
            {
                // First ZIP gets the user's chosen mode; subsequent uploads
                // stack into the same version row via Append. The submission
                // form's caption tells the user this happens — no surprises.
                var mode = i == 0 ? firstMode : BaseAppImportMode.Append;
                var request = new BaseAppImportRequest(
                    Major: major,
                    CumulativeUpdate: cu,
                    ApplicationVersionId: applicationVersionId,
                    // Notes only attached to the first ZIP — Append concatenates
                    // notes from subsequent uploads which we don't want here.
                    Notes: i == 0 ? notes : null,
                    Mode: mode);

                try
                {
                    await using var stream = zipFiles[i].OpenReadStream();
                    var summary = await importer.ImportAsync(stream, request, ct);
                    versionId ??= summary.VersionId;
                    totalParsed += summary.ParsedFiles;
                    totalFailed += summary.FailedFiles;
                    totalReplaced += summary.ReplacedPaths;
                }
                catch (PlanValidationException ex) when (firstError is null)
                {
                    var pair = ex.Errors.First();
                    firstErrorKey = pair.Key;
                    firstError = $"{zipFiles[i].FileName}: {pair.Value}";
                }
            }

            if (firstError is not null && versionId is null)
            {
                // Every ZIP failed; surface the first error and stay on the form.
                Redirect(ctx, firstErrorKey ?? "Zip", firstError);
                return;
            }

            // Stack outcome on the admin page. okKind picks "imported" when
            // only one ZIP landed and "appended" when several stacked.
            var okKind = zipFiles.Length > 1 ? "appended" : "imported";
            var query = $"/admin/object-explorer?ok={okKind}"
                + $"&parsed={totalParsed}"
                + $"&failed={totalFailed}"
                + $"&replaced={totalReplaced}"
                + $"&zips={zipFiles.Length}"
                + $"&id={versionId}";
            if (firstError is not null)
            {
                query += $"&warn={Uri.EscapeDataString(firstError)}";
            }
            ctx.Response.Redirect(query);
        })
        .RequireAuthorization(policy => policy.RequireRole("Admin"))
        .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes))
        .WithMetadata(new RequestFormLimitsAttribute
        {
            MultipartBodyLengthLimit = MaxUploadBytes,
            MultipartHeadersLengthLimit = 32 * 1024,
        });

        app.MapPost("/admin/object-explorer/{id:int}/reindex", async (
            int id,
            HttpContext ctx,
            BaseAppImportService importer,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            try
            {
                await importer.RequestReindexAsync(id, ct);
                ctx.Response.Redirect("/admin/object-explorer?ok=reindex-queued");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                Redirect(ctx, first.Key, first.Value);
            }
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        app.MapPost("/admin/object-explorer/{id:int}/delete", async (
            int id,
            HttpContext ctx,
            BaseAppImportService importer,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            try
            {
                await importer.DeleteAsync(id, ct);
                ctx.Response.Redirect("/admin/object-explorer?ok=deleted");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                Redirect(ctx, first.Key, first.Value);
            }
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        return app;
    }

    private static void Redirect(HttpContext ctx, string errKey, string message)
    {
        ctx.Response.Redirect(
            "/admin/object-explorer?err=" + Uri.EscapeDataString(errKey)
            + "&msg=" + Uri.EscapeDataString(message));
    }
}
