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
            var zipFile = form.Files["Zip"];
            if (zipFile is null || zipFile.Length == 0)
            {
                Redirect(ctx, "Zip", "Choose a ZIP file before submitting.");
                return;
            }

            if (!int.TryParse(form["Major"], out var major)
                || !int.TryParse(form["Minor"], out var minor)
                || !int.TryParse(form["CumulativeUpdate"], out var cu))
            {
                Redirect(ctx, "Version", "Major, Minor, and CumulativeUpdate must be integers.");
                return;
            }

            int? applicationVersionId = null;
            var appVerRaw = form["ApplicationVersionId"].ToString();
            if (!string.IsNullOrEmpty(appVerRaw) && int.TryParse(appVerRaw, out var appVer))
            {
                applicationVersionId = appVer;
            }

            var modeRaw = form["Mode"].ToString();
            if (!Enum.TryParse<BaseAppImportMode>(modeRaw, ignoreCase: true, out var mode))
            {
                mode = BaseAppImportMode.Reject;
            }

            var request = new BaseAppImportRequest(
                Major: major,
                Minor: minor,
                CumulativeUpdate: cu,
                ApplicationVersionId: applicationVersionId,
                Notes: form["Notes"].ToString(),
                Mode: mode);

            try
            {
                await using var stream = zipFile.OpenReadStream();
                var summary = await importer.ImportAsync(stream, request, ct);
                var okKind = summary.WasAppend ? "appended" : "imported";
                ctx.Response.Redirect(
                    $"/admin/object-explorer?ok={okKind}"
                    + $"&parsed={summary.ParsedFiles}"
                    + $"&failed={summary.FailedFiles}"
                    + $"&replaced={summary.ReplacedPaths}"
                    + $"&id={summary.VersionId}");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                Redirect(ctx, first.Key, first.Value);
            }
        })
        .RequireAuthorization(policy => policy.RequireRole("Admin"))
        .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes))
        .WithMetadata(new RequestFormLimitsAttribute
        {
            MultipartBodyLengthLimit = MaxUploadBytes,
            MultipartHeadersLengthLimit = 32 * 1024,
        });

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
