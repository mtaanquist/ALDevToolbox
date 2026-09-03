namespace ALDevToolbox.Endpoints;

/// <summary>
/// Permanent redirects from pre-rename URLs to their current equivalents, so
/// bookmarks and in-the-wild links keep working after a rename. Kept apart from
/// the live endpoint classes so it's obvious these are legacy aliases that can be
/// retired once the old links have aged out.
///
/// Same window as the redirect-only pages in <c>Components/Pages/Legacy/</c>:
/// kept since 2026-09-03; safe to delete after 2027-03-01 if access logs show no
/// hits (issue #700). <c>Components/Pages/Legacy/README.md</c> has the full table
/// and <c>ALDevToolbox.Tests/Routing/LegacyRedirectTests.cs</c> pins it.
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
