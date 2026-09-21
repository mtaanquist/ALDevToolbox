using ALDevToolbox.Services.Palette;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// The command palette's one server call: <c>GET /palette/search?q=</c>,
/// cookie-authenticated, answering data rather than HTML. See
/// <c>.design/command-palette.md</c>, "How it is global".
///
/// <para>A GET with no side effects, so no antiforgery token — but it requires
/// an authenticated user, and an anonymous caller gets a <b>401</b> rather than
/// the cookie handler's redirect to <c>/login</c>. That exception is wired in
/// <c>Startup/AuthenticationRegistration.cs</c> against
/// <see cref="PathPrefix"/>: the palette calls this from a <c>fetch</c>, and a
/// 302 would arrive at the script as a 200 page of sign-in HTML, which it would
/// have no way to tell from results.</para>
/// </summary>
internal static class PaletteEndpoints
{
    /// <summary>
    /// Everything the palette serves lives under here. The cookie handler
    /// matches this prefix to answer 401 instead of redirecting.
    /// </summary>
    public const string PathPrefix = "/palette";

    /// <summary>The search route itself.</summary>
    public const string SearchPath = PathPrefix + "/search";

    /// <summary>
    /// Per-caller rate-limit policy, registered in
    /// <c>Startup/OperationsRegistration.cs</c> beside the others.
    /// </summary>
    public const string SearchRateLimitPolicy = "palette-search";

    public static IEndpointRouteBuilder MapPaletteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(SearchPath, async (
            string? q,
            PaletteSearchService search,
            HttpContext http) =>
        {
            // RequestAborted, not a plain token: when the user types another
            // character the script aborts the in-flight fetch, and this is what
            // turns that into a cancelled SQL command rather than a query
            // nobody is waiting for any more.
            var result = await search.SearchAsync(http.User, q, http.RequestAborted);

            // Results are per-user and change as the organisation does. Nothing
            // downstream should hold one - least of all a shared proxy, where a
            // cached answer would be another tenant's.
            http.Response.Headers.CacheControl = "no-store";
            return Results.Json(result);
        })
        .RequireAuthorization()
        .RequireRateLimiting(SearchRateLimitPolicy);

        return app;
    }
}
