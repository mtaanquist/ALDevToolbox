using System.Text;
using System.Text.Json;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.Translation;
using Microsoft.AspNetCore.Antiforgery;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// The Translator tool's one server endpoint: export the edited XLIFF back out.
/// The browser holds the working file in IndexedDB and POSTs the verbatim
/// original plus the edit overlay here (off the SignalR circuit, so a 3 MB
/// file never touches the Blazor connection). The server splices the edited
/// targets back in byte-faithfully (<see cref="XliffTargetWriter"/>), streams
/// the result back as a download, and feeds the completed pairs into the
/// translation memory. See <c>.design/translator/</c>.
/// </summary>
internal static class TranslatorEndpoints
{
    public static IEndpointRouteBuilder MapTranslatorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/translator/export", async (
            HttpContext ctx,
            IAntiforgery antiforgery,
            TranslationMemoryService memory,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            var form = await ctx.Request.ReadFormAsync(ct);
            var fileName = SanitiseFileName(form["FileName"].ToString());
            var originalXml = form["OriginalXml"].ToString();
            var editsJson = form["Edits"].ToString();

            if (string.IsNullOrEmpty(originalXml))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync("No file content was submitted. Re-open the file and try again.", ct);
                return;
            }

            Dictionary<string, TargetEdit> edits;
            try
            {
                edits = ParseEdits(editsJson);
            }
            catch (JsonException)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync("The edit payload was malformed.", ct);
                return;
            }

            var result = XliffTargetWriter.ApplyEdits(originalXml, edits);

            // Best-effort: feed the finished translations into the memory so
            // they surface next time. Never let a memory hiccup fail the
            // download the user actually asked for.
            try
            {
                await PopulateMemoryAsync(result, memory, ct);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger(typeof(TranslatorEndpoints))
                    .LogWarning(ex, "Translation memory population from export failed; export itself succeeded.");
            }

            var bytes = Encoding.UTF8.GetBytes(result);
            var cd = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
            cd.SetHttpFileName(fileName);
            ctx.Response.Headers.ContentDisposition = cd.ToString();
            ctx.Response.ContentType = "application/xml; charset=utf-8";
            await ctx.Response.Body.WriteAsync(bytes, ct);
        }).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Parses the edit overlay. Shape: a JSON object keyed by trans-unit id,
    /// each value <c>{ "t": "&lt;target&gt;", "s": "&lt;state&gt;" }</c> (state
    /// optional / null).
    /// </summary>
    private static Dictionary<string, TargetEdit> ParseEdits(string json)
    {
        var edits = new Dictionary<string, TargetEdit>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return edits;

        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var target = prop.Value.TryGetProperty("t", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
            string? state = prop.Value.TryGetProperty("s", out var s) ? s.GetString() : null;
            edits[prop.Name] = new TargetEdit(target, string.IsNullOrEmpty(state) ? null : state);
        }
        return edits;
    }

    private static async Task PopulateMemoryAsync(string xml, TranslationMemoryService memory, CancellationToken ct)
    {
        XliffDocument parsed;
        try
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            parsed = AlXliffParser.Parse(ms);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            return; // unparseable result — nothing to learn from
        }

        if (string.IsNullOrEmpty(parsed.SourceLanguage)) return;
        var origin = string.IsNullOrEmpty(parsed.OriginalName) ? "Translator" : parsed.OriginalName;
        var pairs = parsed.Units
            .Where(u => !string.IsNullOrEmpty(u.TargetText))
            .Select(u => new TranslationMemoryUpsert(
                parsed.SourceLanguage!,
                parsed.TargetLanguage,
                u.SourceText,
                u.TargetText,
                AlXliffParser.BucketKind(u.Hint),
                origin));
        await memory.UpsertAsync(pairs, ct);
    }

    /// <summary>Strips path separators and control characters so the download name is safe.</summary>
    private static string SanitiseFileName(string name)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(name)) return "translation.xlf";
        var slash = name.LastIndexOfAny(new[] { '/', '\\' });
        if (slash >= 0) name = name[(slash + 1)..];
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        if (string.IsNullOrWhiteSpace(name)) return "translation.xlf";
        if (!name.EndsWith(".xlf", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".xliff", StringComparison.OrdinalIgnoreCase))
        {
            name += ".xlf";
        }
        return name;
    }
}
