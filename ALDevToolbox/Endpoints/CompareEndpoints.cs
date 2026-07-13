using System.Text.Json;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Diff;
using DiffPlex.DiffBuilder;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// The anonymous read-side endpoint the standalone Compare tool hits from
/// <c>source-viewer.js</c> to re-diff its two editable panes as the user types.
/// It reuses the same DiffPlex + <see cref="SideBySideDiffSerializer"/> pipeline
/// the read-only Object Explorer compare page renders, so both surfaces stay
/// visually identical. Stateless and side-effect-free (no DB, no auth), matching
/// the tool's account-free design — the SSR page owns no Blazor circuit, so the
/// live diff rides a plain fetch instead. See <c>Components/Pages/Compare.razor</c>.
/// </summary>
internal static class CompareEndpoints
{
    /// <summary>Per-side input cap, shared with Piper (internal tool, generous bound).</summary>
    private const int MaxInputLength = PiperTransform.MaxInputLength;

    public static IEndpointRouteBuilder MapCompareEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/compare/diff", (CompareDiffRequest request) =>
        {
            var left = request.Left ?? string.Empty;
            var right = request.Right ?? string.Empty;

            if (left.Length > MaxInputLength || right.Length > MaxInputLength)
            {
                var over = Math.Max(left.Length, right.Length);
                return Results.Json(new
                {
                    error = $"One of the texts is too large ({over:N0} characters). Trim it under {MaxInputLength:N0} and try again.",
                });
            }

            var model = SideBySideDiffBuilder.Diff(left, right);
            var summary = SideBySideDiffSerializer.Summarize(model);

            // Each Serialize* returns a JSON string; embed it as raw JSON (not a
            // re-encoded string) so the client receives real arrays.
            static JsonElement Raw(string json)
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.Clone();
            }

            return Results.Json(new
            {
                left = new
                {
                    diff = Raw(SideBySideDiffSerializer.SerializeSide(model.OldText)),
                    fillers = Raw(SideBySideDiffSerializer.SerializeFillers(model.OldText)),
                    wordDiff = Raw(SideBySideDiffSerializer.SerializeWordDiff(model.OldText)),
                },
                right = new
                {
                    diff = Raw(SideBySideDiffSerializer.SerializeSide(model.NewText)),
                    fillers = Raw(SideBySideDiffSerializer.SerializeFillers(model.NewText)),
                    wordDiff = Raw(SideBySideDiffSerializer.SerializeWordDiff(model.NewText)),
                },
                summary = new
                {
                    added = summary.Added,
                    removed = summary.Removed,
                    modified = summary.Modified,
                    identical = summary.Identical,
                },
            });
        })
        .AllowAnonymous()
        .DisableAntiforgery()
        // Two 50 MB sides plus JSON overhead — lift the default Kestrel body cap.
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(130_000_000));

        return app;
    }

    /// <summary>The two texts to diff. Both optional; empty means "no content yet".</summary>
    public sealed record CompareDiffRequest(string? Left, string? Right);
}
