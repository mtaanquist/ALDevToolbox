using System.IO.Compression;
using System.Text;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Download endpoints for the Artifacts tool: one compiled <c>.app</c>, a build's
/// "Download all" zip, and the raw build log. Any signed-in user who can see the
/// owning project may download its builds, so these require authentication but not
/// project ownership. Two fences are re-checked server-side by
/// <see cref="ArtifactService"/>, which runs on the request's org-filtered
/// <c>AppDbContext</c>: a build belonging to another org returns 404, and a build
/// of a Private project the caller has no grant on throws
/// <see cref="ProjectAccessDeniedException"/>, which each handler turns into the
/// same 404 — a refusal that told the two apart would confirm the project exists.
/// GETs carry no antiforgery token (they're plain download links, no state change).
/// See <c>.design/artifacts.md</c> and <c>.design/teams-and-visibility.md</c>.
/// </summary>
internal static class ArtifactEndpoints
{
    public static IEndpointRouteBuilder MapArtifactEndpoints(this IEndpointRouteBuilder app)
    {
        // One deliverable.
        app.MapGet("/artifacts/build/{buildId:int}/app/{artifactId:int}",
            async (int buildId, int artifactId, ArtifactService artifacts, CancellationToken ct) =>
            {
                DownloadFile? file;
                try
                {
                    file = await artifacts.GetArtifactBytesAsync(buildId, artifactId, ct);
                }
                catch (ProjectAccessDeniedException)
                {
                    return Results.NotFound();
                }
                if (file is null) return Results.NotFound();
                return Results.File(file.Content, "application/octet-stream", SanitiseFileName(file.FileName));
            })
            .RequireAuthorization();

        // Every deliverable of a build, zipped.
        app.MapGet("/artifacts/build/{buildId:int}/all",
            async (int buildId, ArtifactService artifacts, CancellationToken ct) =>
            {
                List<DownloadFile> files;
                try
                {
                    files = await artifacts.GetAllArtifactBytesAsync(buildId, ct);
                }
                catch (ProjectAccessDeniedException)
                {
                    return Results.NotFound();
                }
                if (files.Count == 0) return Results.NotFound();

                using var buffer = new MemoryStream();
                using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var f in files)
                    {
                        var entry = zip.CreateEntry(SanitiseFileName(f.FileName), CompressionLevel.Fastest);
                        await using var es = entry.Open();
                        await es.WriteAsync(f.Content, ct);
                    }
                }
                return Results.File(buffer.ToArray(), "application/zip", $"build-{buildId}-apps.zip");
            })
            .RequireAuthorization();

        // The raw build log.
        app.MapGet("/artifacts/build/{buildId:int}/log",
            async (int buildId, ArtifactService artifacts, CancellationToken ct) =>
            {
                RawLog? log;
                try
                {
                    log = await artifacts.GetRawLogAsync(buildId, ct);
                }
                catch (ProjectAccessDeniedException)
                {
                    return Results.NotFound();
                }
                if (log is null) return Results.NotFound();
                return Results.File(Encoding.UTF8.GetBytes(log.Content), "text/plain; charset=utf-8", log.FileName);
            })
            .RequireAuthorization();

        return app;
    }
}
