using ALDevToolbox.Services.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// The lightweight read-side <c>/api/object-explorer/*</c> GET endpoints the
/// static-SSR source viewer hits from <c>source-viewer.js</c> — go-to-definition,
/// find-in-file, raw download, the SymbolReference stream, outline dependencies,
/// and the find-references session lifecycle. Split out of
/// <see cref="ObjectExplorerEndpoints"/> (which owns the mutating admin POSTs)
/// and registered from it, so <c>Program.cs</c>'s single
/// <c>MapObjectExplorerEndpoints()</c> call still wires everything up.
///
/// These are <c>RequireAuthorization()</c> (any signed-in org user can read),
/// unlike the authoring POSTs which are <c>Admin,Editor</c>-only.
/// </summary>
internal static class ObjectExplorerViewerEndpoints
{
    public static IEndpointRouteBuilder MapObjectExplorerViewerEndpoints(this IEndpointRouteBuilder app)
    {
        // ── JSON read-side endpoints for the static-SSR source viewer ──
        // Replace the Blazor-callback path used by the legacy interactive
        // viewer. The static page's source-viewer.js hits these on Cmd/Ctrl
        // click and right-click "Find in this file" so no SignalR circuit
        // is required for in-document navigation. See
        // .design/source-viewer-redesign.md.
        app.MapGet("/api/object-explorer/files/{fileId:long}/goto", async (
            long fileId,
            int line,
            int column,
            SourceViewerService viewer,
            CancellationToken ct) =>
        {
            var target = await viewer.GoToDefinitionAsync(fileId, line, column, ct);
            return target is null ? Results.NoContent() : Results.Ok(target);
        }).RequireAuthorization();

        app.MapGet("/api/object-explorer/files/{fileId:long}/find-in-file", async (
            long fileId,
            int line,
            int column,
            SourceViewerService viewer,
            CancellationToken ct) =>
        {
            var result = await viewer.FindInFileAsync(fileId, line, column, ct);
            return result is null ? Results.NoContent() : Results.Ok(result);
        }).RequireAuthorization();

        // Download the raw source for a file. Streams the stored content with
        // a Content-Disposition: attachment header so the browser saves it
        // under the file's path basename rather than rendering it inline.
        app.MapGet("/api/object-explorer/files/{fileId:long}/download", async (
            long fileId,
            SourceViewerService viewer,
            CancellationToken ct) =>
        {
            var file = await viewer.GetFileAsync(fileId, ct);
            if (file is null) return Results.NotFound();
            var fileName = file.Path;
            var slash = fileName.LastIndexOf('/');
            if (slash >= 0) fileName = fileName[(slash + 1)..];
            if (string.IsNullOrEmpty(fileName)) fileName = $"file-{fileId}.al";
            var bytes = System.Text.Encoding.UTF8.GetBytes(file.Content ?? string.Empty);
            return Results.File(bytes, "text/plain; charset=utf-8", fileName);
        }).RequireAuthorization();

        // Stream the stored SymbolReference.json for one module — for debugging
        // resolver errors. Written straight to the response via a chunked
        // StreamWriter rather than buffering as bytes, so a base-app symbol
        // file (tens of MB) doesn't allocate the 6×-worst-case escape buffer
        // System.Text.Json would need if this travelled inline through the MCP
        // tool's JSON response (which OOMed). EF query filters scope to the
        // caller's org. Org users see this via the MCP tool's returned URL.
        app.MapGet("/api/object-explorer/release/{releaseId:int}/modules/{moduleId:long}/symbol-reference",
            async (int releaseId, long moduleId,
                ALDevToolbox.Data.AppDbContext db,
                HttpContext ctx,
                CancellationToken ct) =>
        {
            var match = await db.OeModules.AsNoTracking()
                .Where(m => m.Id == moduleId && m.ReleaseId == releaseId)
                .Select(m => new
                {
                    m.Name,
                    Label = m.Release!.Label,
                    Hash = m.SymbolReferenceContentHash,
                    Content = m.SymbolReferenceContent != null ? m.SymbolReferenceContent.Content : null,
                })
                .FirstOrDefaultAsync(ct);

            if (match is null) return Results.NotFound();
            if (match.Hash is null || match.Content is null) return Results.NotFound();

            var safe = SanitiseFileName(match.Label) + "-" + SanitiseFileName(match.Name) + ".SymbolReference.json";
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{safe}\"";
            await using var writer = new StreamWriter(ctx.Response.Body, System.Text.Encoding.UTF8, bufferSize: 64 * 1024, leaveOpen: true);
            await writer.WriteAsync(match.Content.AsMemory(), ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
            return Results.Empty;
        }).RequireAuthorization();

        // Outline dependencies (#148): the file viewer's outline lazy-loads
        // "Using" and "Used by" sections after the static-SSR paint. One
        // round-trip per page load.
        app.MapGet("/api/object-explorer/files/{fileId:long}/dependencies", async (
            long fileId,
            ReferenceQueryService references,
            CancellationToken ct) =>
        {
            var result = await references.GetFileDependenciesAsync(fileId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireAuthorization();

        // ── References-session endpoints ──────────────────────────────
        // Persist a Find-references search across file navigations.
        // The token returned here travels in the URL as ?refSet=…, and
        // the source viewer page reads it server-side to render the
        // persistent References panel. See ReferenceSessionService.
        // GET (rather than POST) because the action is idempotent for a
        // given symbol id + user: the cache key is per-user, and re-minting
        // returns a fresh token without altering DB state. No antiforgery
        // headache for a fetch() call from source-viewer.js.
        app.MapGet("/api/object-explorer/references/sessions/from-symbol/{symbolId:long}", async (
            long symbolId,
            HttpContext ctx,
            ReferenceSessionService sessions,
            CancellationToken ct,
            // The Release the user is viewing from. Lets a base object opened
            // from a customer Release seed find-references at the customer
            // Release so its own code is included. Optional — defaults to the
            // object's home Release. See ReferenceSessionService.
            int? from = null) =>
        {
            var owner = OwnerKey(ctx);
            if (owner is null) return Results.Unauthorized();
            var session = await sessions.CreateFromSymbolAsync(symbolId, owner, from, ct);
            return session is null ? Results.NotFound() : Results.Ok(session);
        }).RequireAuthorization();

        // "Find System References" on an object: every built-in / system
        // method call (Insert / Modify / SetRange / …) on it, from the
        // separate oe_module_system_references table. Renders through the same
        // panel as Find references. The id is an oe_module_objects row.
        app.MapGet("/api/object-explorer/system-references/sessions/from-object/{objectId:long}", async (
            long objectId,
            HttpContext ctx,
            ReferenceSessionService sessions,
            CancellationToken ct,
            int? from = null) =>
        {
            var owner = OwnerKey(ctx);
            if (owner is null) return Results.Unauthorized();
            var session = await sessions.CreateSystemReferencesFromObjectAsync(objectId, owner, from, ct);
            return session is null ? Results.NotFound() : Results.Ok(session);
        }).RequireAuthorization();

        // Member-scoped find: outline right-click on a procedure / field /
        // trigger row mints a session that bundles declarations + indirect
        // owner-type refs + (eventually) method-call refs. The symbolId
        // here is an oe_module_symbols row, not an object id.
        app.MapGet("/api/object-explorer/references/sessions/from-member-symbol/{symbolId:long}", async (
            long symbolId,
            HttpContext ctx,
            ReferenceSessionService sessions,
            CancellationToken ct,
            int? from = null) =>
        {
            var owner = OwnerKey(ctx);
            if (owner is null) return Results.Unauthorized();
            var session = await sessions.CreateFromMemberSymbolAsync(symbolId, owner, from, ct);
            return session is null ? Results.NotFound() : Results.Ok(session);
        }).RequireAuthorization();

        app.MapGet("/api/object-explorer/references/sessions/{token}", (
            string token,
            HttpContext ctx,
            ReferenceSessionService sessions) =>
        {
            var owner = OwnerKey(ctx);
            if (owner is null) return Results.Unauthorized();
            var session = sessions.Get(token, owner);
            return session is null ? Results.NotFound() : Results.Ok(session);
        }).RequireAuthorization();

        // Right-click "Find references" on a non-declaration token: the
        // server inspects the word at (line, column) and resolves it to a
        // same-Release object before minting the session.
        app.MapGet("/api/object-explorer/references/sessions/at-position", async (
            long fileId,
            int line,
            int column,
            HttpContext ctx,
            ReferenceSessionService sessions,
            CancellationToken ct,
            int? from = null) =>
        {
            var owner = OwnerKey(ctx);
            if (owner is null) return Results.Unauthorized();
            var session = await sessions.CreateAtPositionAsync(fileId, line, column, owner, from, ct);
            return session is null ? Results.NoContent() : Results.Ok(session);
        }).RequireAuthorization();

        return app;
    }
}
