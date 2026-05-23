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
        app.MapPost("/generate/workspace", async (HttpContext ctx, GenerationService gen, ApplicationVersionService versions, IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var (resolvedApp, resolvedRuntime) = await ResolveVersionAsync(
                form["ApplicationVersion"].ToString().Trim(),
                form["RuntimeVersion"].ToString().Trim(),
                versions, ctx, form, ct);
            // Application and runtime resolve in lock-step (both null on the
            // error path, both set otherwise). Check both so the assignment
            // below doesn't need a null-forgiving `!`.
            if (resolvedApp is null || resolvedRuntime is null) return;
            var plan = new ProjectPlan(
                TemplateKey: form["TemplateKey"].ToString(),
                WorkspaceName: form["WorkspaceName"].ToString().Trim(),
                ExtensionPrefix: form["ExtensionPrefix"].ToString().Trim(),
                Brief: form["Brief"].ToString().Trim(),
                Description: form["Description"].ToString().Trim(),
                ApplicationVersion: resolvedApp,
                RuntimeVersion: resolvedRuntime,
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
                    .ToList(),
                TenantId: form["TenantId"].ToString().Trim());

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
            catch (Exception ex) when (!ctx.Response.HasStarted)
            {
                // A non-validation fault before the ZIP started streaming —
                // surface a clean 500 instead of letting a half-written
                // attachment reach the client. Once the body has started we
                // can't change the status, so that case falls through to the
                // framework (which logs and aborts the response).
                loggerFactory.CreateLogger(typeof(GenerationEndpoints)).LogError(ex, "Workspace generation failed.");
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                SetGenerationCompleteCookie(ctx, form["GenToken"].ToString());
                await ctx.Response.WriteAsync("Generation failed unexpectedly. Please try again or contact an administrator.", ct);
            }
        }).RequireAuthorization();

        app.MapPost("/generate/extension", async (HttpContext ctx, GenerationService gen, OrganizationConfigService orgConfig, ApplicationVersionService appVersions, IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var (resolvedApp, resolvedRuntime) = await ResolveVersionAsync(
                form["ApplicationVersion"].ToString().Trim(),
                form["RuntimeVersion"].ToString().Trim(),
                appVersions, ctx, form, ct);
            // App + runtime resolve in lock-step; check both to avoid a
            // null-forgiving `!` on the assignment below.
            if (resolvedApp is null || resolvedRuntime is null) return;

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

            // Publisher is no longer a form input — it always comes from
            // the org-level default the admin set on
            // /admin/configuration/defaults. Resolving it here keeps the
            // form lean and matches the workspace flow's policy: there's
            // exactly one publisher per org, configured in one place.
            var orgPublisher = (await orgConfig.GetCurrentAsync(ct)).Settings.DefaultPublisher;

            var plan = new StandaloneExtensionPlan(
                TemplateKey: form["TemplateKey"].ToString(),
                ExtensionName: form["ExtensionName"].ToString().Trim(),
                Brief: form["Brief"].ToString().Trim(),
                Description: form["Description"].ToString().Trim(),
                ApplicationVersion: resolvedApp,
                RuntimeVersion: resolvedRuntime,
                IdRangeFrom: int.TryParse(form["IdRangeFrom"], out var idFrom) ? idFrom : 0,
                IdRangeTo: int.TryParse(form["IdRangeTo"], out var idTo) ? idTo : 0,
                IncludeExamples: form["IncludeExamples"] == "true" || form["IncludeExamples"] == "on",
                Publisher: orgPublisher,
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
            catch (Exception ex) when (!ctx.Response.HasStarted)
            {
                // Non-validation fault before the ZIP started streaming — clean
                // 500 rather than a half-written attachment. See the workspace
                // endpoint for the streaming-already-started caveat.
                loggerFactory.CreateLogger(typeof(GenerationEndpoints)).LogError(ex, "Extension generation failed.");
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                SetGenerationCompleteCookie(ctx, form["GenToken"].ToString());
                await ctx.Response.WriteAsync("Generation failed unexpectedly. Please try again or contact an administrator.", ct);
            }
        }).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Resolves the form-posted application/runtime version pair, swapping
    /// the <see cref="ApplicationVersionService.LatestSentinel"/> sentinel
    /// for the highest-ordered active catalogue row when the user picked
    /// "Latest". The sentinel travels in both fields together — they swap
    /// in lock-step so the resulting <c>app.json</c> stays internally
    /// consistent. Returns <c>(null, null)</c> after writing a 400
    /// response when the catalogue is empty so the caller can short-
    /// circuit cleanly.
    /// </summary>
    private static async Task<(string? Application, string? Runtime)> ResolveVersionAsync(
        string formApplication,
        string formRuntime,
        ApplicationVersionService versions,
        HttpContext ctx,
        Microsoft.AspNetCore.Http.IFormCollection form,
        CancellationToken ct)
    {
        var isLatest = string.Equals(formApplication, ApplicationVersionService.LatestSentinel, StringComparison.Ordinal)
            || string.Equals(formRuntime, ApplicationVersionService.LatestSentinel, StringComparison.Ordinal);
        if (!isLatest) return (formApplication, formRuntime);

        var latest = await versions.GetLatestAsync(ct);
        if (latest is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            SetGenerationCompleteCookie(ctx, form["GenToken"].ToString());
            await ctx.Response.WriteAsync(
                "The submitted form failed validation:\n\n"
                + "  - ApplicationVersion: \"Latest\" requires at least one active application-version row. Add one under /admin/application-versions.",
                ct);
            return (null, null);
        }
        return (latest.Application, latest.Runtime);
    }
}
