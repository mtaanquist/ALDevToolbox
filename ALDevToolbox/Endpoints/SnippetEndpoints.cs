using System.IO.Compression;
using System.Text;
using ALDevToolbox.Services;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

internal static class SnippetEndpoints
{
    public static IEndpointRouteBuilder MapSnippetEndpoints(this IEndpointRouteBuilder app)
    {
        // ZIP download for all files in a snippet. GETs don't need
        // antiforgery; the route runs under the standard cookie auth +
        // EF tenant filter, so a user can only see snippets in their own
        // org. 404 collapses both "doesn't exist" and "exists in another
        // org" into the same response.
        app.MapGet("/api/snippets/{id:int}/download", async (
            int id,
            HttpContext ctx,
            SnippetService snippets,
            CancellationToken ct) =>
        {
            var snippet = await snippets.GetAsync(id, ct);
            if (snippet is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var fileName = BuildArchiveFileName(snippet.Title, snippet.Id);
            WriteAttachmentHeaders(ctx, fileName);

            using var archive = new ZipArchive(ctx.Response.Body, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var file in snippet.Files)
            {
                var entry = archive.CreateEntry(file.FileName, CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                await writer.WriteAsync(file.Content);
            }
        }).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Lower-cases and slugifies the snippet title for the ZIP filename.
    /// Falls back to <c>snippet-{id}</c> when the title slugifies to empty
    /// (titles made entirely of non-ASCII letters, punctuation, etc).
    /// </summary>
    internal static string BuildArchiveFileName(string title, int id)
    {
        var slug = Slugify(title);
        if (string.IsNullOrEmpty(slug))
        {
            slug = $"snippet-{id}";
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
