namespace ALDevToolbox.Endpoints;

/// <summary>
/// Permanent redirects from pre-rename URLs to their current equivalents, so
/// bookmarks and in-the-wild links keep working after a rename. Kept apart from
/// the live endpoint classes so it's obvious these are legacy aliases that can be
/// retired once the old links have aged out.
/// </summary>
public static class LegacyRedirectEndpoints
{
    public static IEndpointRouteBuilder MapLegacyRedirects(this IEndpointRouteBuilder app)
    {
        // The Workspace/Extension generator moved from /projects/* to /templates/*
        // when /projects became the home of the new Projects (build) tool. Only the
        // extension generator gets a redirect — the old /projects/new now belongs to
        // the new-project page, so it can't double as a generator alias. The
        // workspace generator's `?template=` query is preserved. See
        // .design/artifacts.md.
        app.MapGet("/projects/extension", (HttpContext ctx) =>
            Results.LocalRedirect("/templates/extension" + (ctx.Request.QueryString.Value ?? string.Empty), permanent: true));

        // Projects became Solutions: "project" now means Business Central's own
        // project-accounting area to the people using this, so the customer engagement
        // it used to name needed a word of its own. The tool had only ever been on
        // staging, but the links above it (Pipelines, Releases, Upgrades) were already
        // pointing here, so the old paths keep resolving. The int constraint is what
        // keeps /projects/extension above out of this route's reach.
        app.MapGet("/projects", () => Results.LocalRedirect("/solutions", permanent: true));
        app.MapGet("/projects/new", () => Results.LocalRedirect("/solutions/new", permanent: true));
        app.MapGet("/projects/{id:int}", (int id) => Results.LocalRedirect($"/solutions/{id}", permanent: true));

        app.MapGet("/snippets", () => Results.LocalRedirect("/cookbook", permanent: true));
        app.MapGet("/snippets/suggest", () => Results.LocalRedirect("/cookbook/suggest", permanent: true));
        app.MapGet("/snippets/{id:int}", (int id) => Results.LocalRedirect($"/cookbook/{id}", permanent: true));
        app.MapGet("/admin/snippets", () => Results.LocalRedirect("/admin/cookbook", permanent: true));
        app.MapGet("/admin/snippets/new", () => Results.LocalRedirect("/admin/cookbook/new", permanent: true));
        app.MapGet("/admin/snippets/{id:int}", (int id) => Results.LocalRedirect($"/admin/cookbook/{id}", permanent: true));
        app.MapGet("/admin/snippets/suggestions", (HttpContext ctx) =>
        {
            var focus = ctx.Request.Query["focus"].ToString();
            var query = string.IsNullOrEmpty(focus) ? string.Empty : $"?focus={focus}";
            return Results.LocalRedirect("/admin/cookbook/suggestions" + query, permanent: true);
        });
        app.MapGet("/api/snippets/{id:int}/download", (int id) =>
            Results.LocalRedirect($"/api/cookbook/{id}/download", permanent: true));
        return app;
    }
}
