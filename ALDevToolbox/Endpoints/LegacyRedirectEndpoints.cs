namespace ALDevToolbox.Endpoints;

/// <summary>
/// Permanent redirects from the pre-rename <c>/snippets</c> URLs to their
/// <c>/cookbook</c> equivalents, so bookmarks and in-the-wild links keep
/// working after the Cookbook rename. Kept apart from the live
/// <c>CookbookEndpoints</c> so it's obvious these are legacy aliases that can
/// be retired once the old links have aged out.
/// </summary>
public static class LegacyRedirectEndpoints
{
    public static IEndpointRouteBuilder MapLegacyRedirects(this IEndpointRouteBuilder app)
    {
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
