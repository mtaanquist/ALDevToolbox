using System.IO.Compression;
using System.Text;
using ALDevToolbox.Services;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

internal static class CookbookEndpoints
{
    public static IEndpointRouteBuilder MapCookbookEndpoints(this IEndpointRouteBuilder app)
    {
        // ZIP download for all files in a recipe. GETs don't need
        // antiforgery; the route runs under the standard cookie auth +
        // EF tenant filter, so a user can only see recipes in their own
        // org. 404 collapses both "doesn't exist" and "exists in another
        // org" into the same response. Each file's RelativePath is joined
        // with `/` so ZipArchive materialises folders automatically.
        //
        // A `customer` query value is required: the download modal collects it
        // and we record the download against that customer (RecordDownloadAsync)
        // so a later bug in a recipe can be traced to who received it. We record
        // BEFORE writing the ZIP body — once the stream starts the status code
        // is fixed. The recording GET has a side effect by design; the download
        // is a navigation and the trace is the point.
        //
        // GETs can't carry an antiforgery token, so the attribution write would
        // otherwise be CSRF-reachable: another origin could navigate the
        // victim's session here and record a download for an arbitrary customer
        // string. Gate the write on the Sec-Fetch-Site fetch-metadata header —
        // the modal's own location.assign is `same-origin` (and an address-bar
        // navigation is `none`); a forged cross-site navigation is `cross-site`,
        // so we serve the ZIP but skip the attribution. See #414.
        app.MapGet("/api/cookbook/{id:int}/download", async (
            int id,
            HttpContext ctx,
            RecipeService recipes,
            IOrganizationContext orgContext,
            CancellationToken ct) =>
        {
            var recipe = await recipes.GetAsync(id, ct);
            if (recipe is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var customer = (ctx.Request.Query["customer"].ToString() ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(customer))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync("A customer name is required to download a recipe.", ct);
                return;
            }
            // Skip the attribution write on a forged cross-site navigation; a
            // same-origin / direct download still records. The ZIP itself is
            // served either way (it's org-scoped and behind auth).
            var fetchSite = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
            if (!string.Equals(fetchSite, "cross-site", StringComparison.OrdinalIgnoreCase))
            {
                await recipes.RecordDownloadAsync(id, customer, orgContext.CurrentUserId, ct);
            }

            var fileName = BuildArchiveFileName(recipe.Title, recipe.Id);
            WriteAttachmentHeaders(ctx, fileName);

            using var archive = new ZipArchive(ctx.Response.Body, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var file in recipe.Files)
            {
                var entryPath = string.IsNullOrEmpty(file.RelativePath)
                    ? file.FileName
                    : file.RelativePath + "/" + file.FileName;
                var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                await writer.WriteAsync(file.Content);
            }
        }).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Lower-cases and slugifies the recipe title for the ZIP filename.
    /// Falls back to <c>recipe-{id}</c> when the title slugifies to empty
    /// (titles made entirely of non-ASCII letters, punctuation, etc).
    /// </summary>
    internal static string BuildArchiveFileName(string title, int id)
    {
        var slug = Slugify(title);
        if (string.IsNullOrEmpty(slug))
        {
            slug = $"recipe-{id}";
        }
        return slug + ".zip";
    }

    private static string Slugify(string input)
    {
        var sb = new StringBuilder(input.Length);
        var lastWasDash = true; // suppress leading dash
        foreach (var raw in input)
        {
            var c = char.ToLowerInvariant(raw);
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(c);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }
        var result = sb.ToString();
        return result.TrimEnd('-');
    }
}
