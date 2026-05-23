using ALDevToolbox.Services;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// While <see cref="BackupService.RestoreAsync"/> is mid-flight,
/// <see cref="MaintenanceModeState"/> is active and every non-SiteAdmin
/// request gets a <c>503</c> with a tiny static body. SiteAdmin requests and
/// the <c>/site-admin</c> area still pass through so the operator can watch the
/// restore; <c>/healthz</c> and <c>/readyz</c> stay reachable so reverse
/// proxies don't flap the container during a long restore.
/// </summary>
internal static class MaintenanceModeMiddleware
{
    public static IApplicationBuilder UseMaintenanceMode(this IApplicationBuilder app)
    {
        app.Use(async (ctx, next) =>
        {
            var maintenance = ctx.RequestServices.GetRequiredService<MaintenanceModeState>();
            if (!maintenance.IsActive)
            {
                await next();
                return;
            }

            var path = ctx.Request.Path;
            if (path.StartsWithSegments("/healthz")
                || path.StartsWithSegments("/readyz")
                || path.StartsWithSegments("/site-admin"))
            {
                await next();
                return;
            }
            if (ctx.User?.FindFirst(HttpOrganizationContext.SiteAdminClaim)?.Value == "true")
            {
                await next();
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            ctx.Response.Headers.RetryAfter = "30";
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(BuildBody(maintenance));
        });
        return app;
    }

    private static string BuildBody(MaintenanceModeState maintenance)
    {
        var reason = System.Net.WebUtility.HtmlEncode(
            maintenance.Reason ?? "The application is restoring from a backup.");
        return "<!doctype html><html><head><title>Maintenance · AL Dev Toolbox</title></head>"
            + "<body style=\"font-family: system-ui, sans-serif; padding: 2rem;\">"
            + "<h1>Maintenance in progress</h1>"
            + $"<p>{reason}</p>"
            + $"<p>Started: {maintenance.StartedAtUtc:yyyy-MM-dd HH:mm 'UTC'}. The service will return shortly.</p>"
            + "</body></html>";
    }
}
