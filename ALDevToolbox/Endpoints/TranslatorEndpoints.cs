using System.Text;
using System.Text.Json;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.Translation;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// The Translator tool's server endpoints:
/// <list type="bullet">
/// <item>Export the edited XLIFF back out. The browser holds the working file in
/// IndexedDB and POSTs the verbatim original plus the edit overlay here (off the
/// SignalR circuit, so even a large file never touches the Blazor connection).
/// The server splices the edited targets back in byte-faithfully
/// (<see cref="XliffTargetWriter"/>), streams the result back as a download, and
/// feeds the completed pairs into the translation memory.</item>
/// <item>Import an XLIFF straight into the translation memory (admin) - e.g. a
/// Microsoft language-pack file - without opening it in the editor first.</item>
/// </list>
/// Both intake up to a 100 MB file: the export posts the whole original XML as a
/// form <em>value</em>, so this endpoint lifts <c>ValueLengthLimit</c> (default
/// ~4 MB) as well as the body/multipart caps. See <c>.design/translator/</c>.
/// </summary>
internal static class TranslatorEndpoints
{
    // 100 MB is the advertised file ceiling; give the request body headroom above
    // that for the edits overlay and multipart framing.
    private const long MaxRequestBytes = 128L * 1024 * 1024;

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
        })
        .RequireAuthorization()
        .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes))
        // OriginalXml arrives as a form *value*, not a file, so ValueLengthLimit
        // (default ~4 MB) is the real ceiling - raise it alongside the body caps.
        .WithMetadata(new RequestFormLimitsAttribute
        {
            ValueLengthLimit = (int)MaxRequestBytes,
            MultipartBodyLengthLimit = MaxRequestBytes,
        });

        // ── Import an XLIFF into the translation memory (admin) ────────────
        // Seed the org's memory from a standalone .xlf (e.g. a Microsoft
        // language-pack file) without opening it in the editor. Static-rendered
        // page + multipart POST so the ~80 MB body streams through Kestrel, not
        // the SignalR circuit. Reuses the same parse -> UpsertAsync path the
        // export uses; origin is the uploaded file name so a pair's source is
        // visible on the memory page.
        // POST target is distinct from the page route (/admin/translation-memory/import)
        // so it doesn't collide with the Blazor component endpoint, which isn't
        // HTTP-method-constrained. Same split AdminReleaseTranslations uses.
        app.MapPost("/admin/translation-memory/import/upload", async (
            HttpContext ctx,
            IAntiforgery antiforgery,
            TranslationMemoryService memory,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            var form = await ctx.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("XliffFile");
            if (file is null || file.Length == 0)
            {
                RedirectMemoryImport(ctx, "Pick an .xlf or .xliff file before submitting.");
                return;
            }

            XliffDocument parsed;
            try
            {
                await using var stream = file.OpenReadStream();
                parsed = AlXliffParser.Parse(stream);
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
            {
                RedirectMemoryImport(ctx, "That file isn't a valid XLIFF. Export it again and retry.");
                return;
            }

            if (string.IsNullOrEmpty(parsed.SourceLanguage))
            {
                RedirectMemoryImport(ctx,
                    "This file has no source language, so there are no source → target pairs to learn. "
                    + "Use a bilingual XLIFF (one with both a source-language and a target-language).");
                return;
            }

            var origin = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(origin)) origin = "Imported XLIFF";
            var inserted = await memory.UpsertAsync(BuildPairs(parsed, origin), ct);

            ctx.Response.Redirect(
                "/admin/translation-memory/import"
                + $"?ok=imported&count={inserted}"
                + $"&file={Uri.EscapeDataString(origin)}"
                + $"&lang={Uri.EscapeDataString(parsed.TargetLanguage)}");
        })
        .RequireAuthorization(policy => policy.RequireRole(
            HttpOrganizationContext.AdminRole, HttpOrganizationContext.EditorRole))
        .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes))
        .WithMetadata(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaxRequestBytes });

        return app;
    }

    private static void RedirectMemoryImport(HttpContext ctx, string message) =>
        ctx.Response.Redirect(
            "/admin/translation-memory/import?err=" + Uri.EscapeDataString(message));

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
        await memory.UpsertAsync(BuildPairs(parsed, origin), ct);
    }

    /// <summary>
    /// Projects a parsed XLIFF's translated units into memory-upsert rows. Shared
    /// by the export round-trip and the standalone memory import; callers pass the
    /// <paramref name="origin"/> label (the <c>&lt;file original&gt;</c> name on
    /// export, the uploaded file name on import). Caller must have confirmed
    /// <see cref="XliffDocument.SourceLanguage"/> is non-null.
    /// </summary>
    private static IEnumerable<TranslationMemoryUpsert> BuildPairs(XliffDocument parsed, string origin) =>
        parsed.Units
            .Where(u => !string.IsNullOrEmpty(u.TargetText))
            .Select(u => new TranslationMemoryUpsert(
                parsed.SourceLanguage!,
                parsed.TargetLanguage,
                u.SourceText,
                u.TargetText,
                AlXliffParser.BucketKind(u.Hint),
                origin));

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
