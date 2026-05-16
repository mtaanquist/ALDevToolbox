using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using Microsoft.AspNetCore.Antiforgery;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

internal static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // /admin/configuration/logo/preview serves the current org's logo bytes
        // so the configuration page can display the existing logo. The page
        // itself commits its edits through Blazor service calls.
        app.MapGet("/admin/configuration/logo/preview", async (
            HttpContext ctx, OrganizationConfigService config, CancellationToken ct) =>
        {
            var snapshot = await config.GetCurrentAsync(ct);
            if (snapshot.Logo is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            ctx.Response.ContentType = snapshot.Logo.ContentType;
            ctx.Response.Headers.CacheControl = "no-store";
            await ctx.Response.Body.WriteAsync(snapshot.Logo.Content, ct);
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        // /admin/export/import — destructive restore from a TOML snapshot.
        // Lives on the Export page, kept as a server POST so the (large)
        // TOML body is parsed outside the interactive Blazor circuit.
        // Note: tenants never see database backup/restore; this endpoint only
        // replays the per-org configuration TOML.
        app.MapPost("/admin/export/import", async (
            HttpContext ctx, OrganizationConfigService config, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var toml = form["Toml"].ToString();
            var confirmed = form["Confirm"] == "true" || form["Confirm"] == "on";
            if (!confirmed)
            {
                ctx.Response.Redirect(
                    $"{RouteConstants.AdminExport}?{RouteConstants.ErrQuery}=Confirm&{RouteConstants.MsgQuery}="
                    + Uri.EscapeDataString("Tick the confirmation box before importing."));
                return;
            }
            try
            {
                await config.ImportFromTomlAsync(toml, ct);
                ctx.Response.Redirect($"{RouteConstants.AdminExport}?{RouteConstants.OkQuery}=imported");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                ctx.Response.Redirect(
                    $"{RouteConstants.AdminExport}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Key)}&{RouteConstants.MsgQuery}={Uri.EscapeDataString(first.Value)}");
            }
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        app.MapPost("/admin/export/download", async (HttpContext ctx, ExportService export, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            var archive = await export.ExportAllAsync(ct);
            WriteAttachmentHeaders(ctx, archive.FileName);
            archive.Stream.Position = 0;
            await archive.Stream.CopyToAsync(ctx.Response.Body, ct);
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        // Marks a single runtime template as the per-organisation default. The
        // service enforces the "active and non-deprecated" rule and clears
        // the previous default in the same SaveChanges.
        app.MapPost("/admin/templates/{id:int}/default", async (
            int id, HttpContext ctx, TemplateService templates, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try
            {
                await templates.SetDefaultAsync(id, ct);
                ctx.Response.Redirect($"{RouteConstants.AdminTemplates}?{RouteConstants.OkQuery}=default-set");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                ctx.Response.Redirect(
                    $"{RouteConstants.AdminTemplates}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Key)}&{RouteConstants.MsgQuery}={Uri.EscapeDataString(first.Value)}");
            }
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        return app;
    }
}
