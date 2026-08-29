using System.Text;
using System.Text.Json;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Compatibility shim for MCP clients that reach <c>/mcp</c> through an API
/// gateway which narrows the <c>Accept</c> header to <c>application/json</c>.
/// Copilot Studio fronted by Azure APIM is the case we have seen.
///
/// <para>
/// Streamable HTTP requires a client to accept <em>both</em>
/// <c>application/json</c> and <c>text/event-stream</c>, and the C# SDK
/// enforces that unconditionally — a JSON-only request is refused with
/// <c>406 Not Acceptable: Client must accept both application/json and
/// text/event-stream</c> before any tool runs. The SDK then answers every
/// POST as a one-message SSE body regardless, so simply widening the request
/// header would hand such a client a <c>text/event-stream</c> response it
/// cannot read. This middleware therefore does both halves: it widens the
/// request's Accept, and unwraps the single SSE message back to plain JSON on
/// the way out.
/// </para>
///
/// <para>
/// This is a deliberate workaround for a gap the SDK does not expose a knob
/// for (<c>HttpServerTransportOptions</c> has no Accept or response-format
/// setting as of 2.2.0, and <c>SessionMode</c>/<c>Stateless</c> does not
/// affect either behaviour — both were measured, not assumed). Drop it if the
/// SDK ever grows a supported option.
/// </para>
///
/// <para>
/// Scope is kept as narrow as the problem: POSTs to <c>/mcp</c> whose Accept
/// names <c>application/json</c> and does not name <c>text/event-stream</c>.
/// Anything else — a conforming client, the GET SSE channel, a wildcard
/// Accept — is passed through untouched, so a well-behaved client's bytes are
/// never rewritten.
/// </para>
/// </summary>
internal static class McpAcceptCompatibility
{
    private const string Json = "application/json";
    private const string EventStream = "text/event-stream";

    /// <summary>
    /// Installs the shim. Register it after <c>UseMcpKillSwitch</c> so a
    /// deployment with MCP switched off short-circuits to 404 without this
    /// middleware buffering the response.
    /// </summary>
    public static IApplicationBuilder UseMcpAcceptCompatibility(this IApplicationBuilder app)
    {
        app.Use(async (ctx, next) =>
        {
            if (!NeedsShim(ctx.Request))
            {
                await next();
                return;
            }

            ctx.Request.Headers.Accept = $"{Json}, {EventStream}";

            // Buffer so nothing reaches the client until we know whether the
            // SDK answered with SSE. Safe here because a stateless POST is a
            // single self-contained message, not a long-lived stream — the
            // streaming channel is GET, which NeedsShim excludes.
            var original = ctx.Response.Body;
            using var buffer = new MemoryStream();
            ctx.Response.Body = buffer;
            try
            {
                await next();
            }
            finally
            {
                ctx.Response.Body = original;
            }

            var logger = ctx.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(McpAcceptCompatibility).FullName!);

            if (IsEventStream(ctx.Response.ContentType)
                && TryUnwrapSingleResponse(buffer, out var payload))
            {
                var bytes = Encoding.UTF8.GetBytes(payload);
                ctx.Response.ContentType = $"{Json}; charset=utf-8";
                ctx.Response.ContentLength = bytes.Length;
                logger.LogDebug(
                    "Unwrapped an SSE MCP reply to {ContentType} for a gateway client on {Path}",
                    Json, ctx.Request.Path);
                await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
                return;
            }

            // Not SSE (an auth 401, the kill-switch 404, an SDK error page) or
            // an SSE body we could not read as one JSON-RPC response. Pass the
            // original bytes through rather than inventing a reply — a shim
            // that guesses here would be worse than the 406 it replaces.
            if (IsEventStream(ctx.Response.ContentType))
            {
                logger.LogWarning(
                    "MCP replied with {ContentType} on {Path} but it did not hold exactly one "
                    + "JSON-RPC response, so it was passed through unconverted; a JSON-only "
                    + "client will not be able to read it",
                    EventStream, ctx.Request.Path);
            }
            ctx.Response.ContentLength = buffer.Length;
            buffer.Position = 0;
            await buffer.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        });
        return app;
    }

    /// <summary>
    /// True only for the narrow case this shim exists for. A request with no
    /// Accept, a wildcard, or one that already names <c>text/event-stream</c>
    /// is left alone — the SDK handles those itself.
    /// </summary>
    private static bool NeedsShim(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method)) return false;
        if (!request.Path.StartsWithSegments("/mcp")) return false;

        var accept = (string?)request.Headers.Accept;
        if (string.IsNullOrWhiteSpace(accept)) return false;
        if (accept.Contains(EventStream, StringComparison.OrdinalIgnoreCase)) return false;
        return accept.Contains(Json, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEventStream(string? contentType) =>
        contentType is not null
        && contentType.StartsWith(EventStream, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Pulls the JSON-RPC response out of an SSE body.
    ///
    /// <para>
    /// A response always carries <c>id</c>; a notification never does (JSON-RPC
    /// 2.0 §4.1). So the last <c>data:</c> payload with an <c>id</c> member is
    /// the reply, and any progress notifications ahead of it are dropped —
    /// which is what a client that cannot read SSE would want. Returns false if
    /// no payload qualifies, leaving the caller to pass the body through
    /// untouched.
    /// </para>
    /// </summary>
    private static bool TryUnwrapSingleResponse(MemoryStream body, out string payload)
    {
        payload = string.Empty;
        body.Position = 0;
        using var reader = new StreamReader(body, Encoding.UTF8, leaveOpen: true);
        var text = reader.ReadToEnd();

        string? found = null;
        var data = new StringBuilder();

        // An SSE event ends at a blank line; a single event may carry several
        // data: lines, which concatenate with newlines.
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (TakeIfResponse(data, ref found)) { /* keep scanning for a later one */ }
                data.Clear();
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            if (data.Length > 0) data.Append('\n');
            data.Append(line.AsSpan(5).TrimStart());
        }
        TakeIfResponse(data, ref found);

        if (found is null) return false;
        payload = found;
        return true;
    }

    private static bool TakeIfResponse(StringBuilder data, ref string? found)
    {
        if (data.Length == 0) return false;
        var candidate = data.ToString();
        if (!HasJsonRpcId(candidate)) return false;
        found = candidate;
        return true;
    }

    private static bool HasJsonRpcId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("id", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
