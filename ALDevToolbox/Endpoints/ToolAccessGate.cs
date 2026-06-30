using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services.Tools;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Route-access gate for the toggleable tools. When a tool is switched off —
/// site-wide by a SiteAdmin, or per-org by an org Admin — a direct navigation
/// to any of that tool's end-user routes (typed URL, reload, enhanced-nav GET)
/// gets a real 404 instead of the page. The sidebar already hides disabled
/// tools, so this is the "URL hacking" backstop: a hidden tool isn't reachable
/// by guessing its address.
///
/// <para>
/// Site state comes from the in-memory <see cref="IToolAvailability"/> singleton;
/// the per-org opt-out is read from the <c>org_disabled_tools</c> cookie claim
/// (no DB hit), so an org change propagates on the next cookie revalidation
/// (~5 min) — matching the existing MCP nav behaviour. Only the tools'
/// end-user route prefixes are gated (see <see cref="ToolCatalog"/>); their
/// <c>/admin/*</c> authoring pages stay reachable. Runs after authentication so
/// the claim is available, and ahead of routing so the 404 re-executes
/// <c>/not-found</c> via <c>UseStatusCodePagesWithReExecute</c>.
/// </para>
/// </summary>
internal static class ToolAccessGate
{
    public static IApplicationBuilder UseToolAccessGate(this IApplicationBuilder app)
    {
        app.Use(async (ctx, next) =>
        {
            var tool = MatchTool(ctx.Request.Path);
            if (tool is null)
            {
                await next();
                return;
            }

            var availability = ctx.RequestServices.GetRequiredService<IToolAvailability>();
            var siteEnabled = availability.IsSiteEnabled(tool.Value);
            // Org opt-out only narrows a site-enabled tool; a site-disabled tool
            // is gone for everyone regardless of the claim.
            var orgDisabled = siteEnabled
                && EndpointHelpers.ReadDisabledTools(ctx.User).Contains(tool.Value);

            if (!siteEnabled || orgDisabled)
            {
                // A plain 404 — UseStatusCodePagesWithReExecute("/not-found")
                // turns it into the NotFound page. Same idiom as the SiteAdmin
                // path guard in Program.cs.
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next();
        });
        return app;
    }

    /// <summary>
    /// Returns the tool whose end-user route prefix the path falls under, or
    /// <see langword="null"/> when the path isn't a gated tool route.
    /// </summary>
    private static ToolKey? MatchTool(PathString path)
    {
        if (!path.HasValue) return null;
        foreach (var tool in ToolCatalog.All)
        {
            foreach (var prefix in tool.RoutePrefixes)
            {
                if (path.StartsWithSegments(prefix)) return tool.Key;
            }
        }
        return null;
    }
}
