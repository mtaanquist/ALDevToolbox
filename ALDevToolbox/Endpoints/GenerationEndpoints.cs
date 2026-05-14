using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using Microsoft.AspNetCore.Antiforgery;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

internal static class GenerationEndpoints
{
    public static IEndpointRouteBuilder MapGenerationEndpoints(this IEndpointRouteBuilder app)
    {
        // File download endpoint for the New Workspace flow. Requires a
        // signed-in user — anonymous access to the generators stopped with M13.
        app.MapPost("/generate/workspace", async (HttpContext ctx, GenerationService gen, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var plan = new ProjectPlan(
                TemplateKey: form["TemplateKey"].ToString(),
                WorkspaceName: form["WorkspaceName"].ToString().Trim(),
                ExtensionPrefix: form["ExtensionPrefix"].ToString().Trim(),
                Brief: form["Brief"].ToString().Trim(),
                Description: form["Description"].ToString().Trim(),
                ApplicationVersion: form["ApplicationVersion"].ToString().Trim(),
                RuntimeVersion: form["RuntimeVersion"].ToString().Trim(),
                CoreIdRangeFrom: int.TryParse(form["CoreIdRangeFrom"], out var cf) ? cf : 0,
                CoreIdRangeTo: int.TryParse(form["CoreIdRangeTo"], out var ctn) ? ctn : 0,
                IncludeExamples: form["IncludeExamples"] == "true" || form["IncludeExamples"] == "on",
                SelectedExtensionPaths: form["SelectedExtensionPaths"]
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Select(s => s!)
                    .ToList(),
                SelectedModuleKeys: form["SelectedModuleKeys"]
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Select(s => s!)
                    .ToList());

            try
            {
                var archive = await gen.GenerateWorkspaceAsync(plan, ct);
                WriteAttachmentHeaders(ctx, archive.FileName);
                SetGenerationCompleteCookie(ctx, form["GenToken"].ToString());
                archive.Stream.Position = 0;
                await archive.Stream.CopyToAsync(ctx.Response.Body, ct);
            }
            catch (PlanValidationException ex)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                SetGenerationCompleteCookie(ctx, form["GenToken"].ToString());
                var body = "The submitted form failed validation:\n\n"
                    + string.Join("\n", ex.Errors.Select(e => $"  - {e.Key}: {e.Value}"));
                await ctx.Response.WriteAsync(body, ct);
            }
        }).RequireAuthorization();

        app.MapPost("/generate/extension", async (HttpContext ctx, GenerationService gen, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);

            var ids = form["DependencyIds"];
            var names = form["DependencyNames"];
            var publishers = form["DependencyPublishers"];
            var versions = form["DependencyVersions"];
            var dependencies = new List<DependencyEntry>(ids.Count);
            for (var i = 0; i < ids.Count; i++)
            {
                dependencies.Add(new DependencyEntry(
                    DepId: ids[i] ?? string.Empty,
                    DepName: i < names.Count ? names[i] ?? string.Empty : string.Empty,
                    DepPublisher: i < publishers.Count ? publishers[i] ?? string.Empty : string.Empty,
                    DepVersion: i < versions.Count ? versions[i] ?? string.Empty : string.Empty));
            }

            var plan = new StandaloneExtensionPlan(
                TemplateKey: form["TemplateKey"].ToString(),
                ExtensionName: form["ExtensionName"].ToString().Trim(),
                Brief: form["Brief"].ToString().Trim(),
                Description: form["Description"].ToString().Trim(),
                ApplicationVersion: form["ApplicationVersion"].ToString().Trim(),
                RuntimeVersion: form["RuntimeVersion"].ToString().Trim(),
                IdRangeFrom: int.TryParse(form["IdRangeFrom"], out var idFrom) ? idFrom : 0,
                IdRangeTo: int.TryParse(form["IdRangeTo"], out var idTo) ? idTo : 0,
                IncludeExamples: form["IncludeExamples"] == "true" || form["IncludeExamples"] == "on",
                Publisher: form["Publisher"].ToString().Trim(),
                Dependencies: dependencies);

            var workspaceName = form["WorkspaceName"].ToString().Trim();
            SiblingWorkspaceContext? sibling = null;
            if (!string.IsNullOrEmpty(workspaceName))
            {
                var workspaceModules = form["WorkspaceModuleKeys"]
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Select(s => s!)
                    .ToList();
                var workspaceFolders = form["WorkspaceFolders"]
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Select(s => s!)
                    .ToList();
                sibling = new SiblingWorkspaceContext(workspaceName, workspaceModules, workspaceFolders);
            }

            try
            {
                var archive = await gen.GenerateExtensionAsync(plan, sibling, ct);
                WriteAttachmentHeaders(ctx, archive.FileName);
                SetGenerationCompleteCookie(ctx, form["GenToken"].ToString());
                archive.Stream.Position = 0;
                await archive.Stream.CopyToAsync(ctx.Response.Body, ct);
            }
            catch (PlanValidationException ex)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                SetGenerationCompleteCookie(ctx, form["GenToken"].ToString());
                var body = "The submitted form failed validation:\n\n"
                    + string.Join("\n", ex.Errors.Select(e => $"  - {e.Key}: {e.Value}"));
                await ctx.Response.WriteAsync(body, ct);
            }
        }).RequireAuthorization();

        return app;
    }
}
