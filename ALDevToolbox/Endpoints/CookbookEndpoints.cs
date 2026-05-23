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
        app.MapGet("/api/cookbook/{id:int}/download", async (
            int id,
            HttpContext ctx,
            RecipeService recipes,
            CancellationToken ct) =>
        {
            var recipe = await recipes.GetAsync(id, ct);
            if (recipe is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
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
