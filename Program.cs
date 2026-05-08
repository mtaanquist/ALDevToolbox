using ALDevToolbox.Components;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.IncludeScopes = false;
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    options.UseUtcTimestamp = true;
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// SQLite connection string. DB_PATH wins, falling back to a file alongside the
// content root for local `dotnet run` so devs don't need to set anything up.
var dbPath = Environment.GetEnvironmentVariable("DB_PATH")
    ?? Path.Combine(builder.Environment.ContentRootPath, "app.db");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddScoped<TemplateService>();
builder.Services.AddScoped<SeedService>();
builder.Services.AddScoped<GenerationService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// File download endpoint for the New Workspace flow. The form posts here and
// the response is a ZIP attachment. Validation errors render an inline error
// page so the user can navigate back; the form fields themselves enforce most
// of the same rules client-side via HTML attributes.
app.MapPost("/generate/workspace", async (HttpContext ctx, GenerationService gen, IAntiforgery antiforgery, CancellationToken ct) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(ctx);
    }
    catch (AntiforgeryValidationException)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        await ctx.Response.WriteAsync("Antiforgery validation failed. Reload the form and try again.", ct);
        return;
    }
    var form = await ctx.Request.ReadFormAsync(ct);
    var plan = new ProjectPlan(
        TemplateKey: form["TemplateKey"].ToString(),
        WorkspaceName: form["WorkspaceName"].ToString().Trim(),
        Brief: form["Brief"].ToString().Trim(),
        Description: form["Description"].ToString().Trim(),
        ApplicationVersion: form["ApplicationVersion"].ToString().Trim(),
        RuntimeVersion: form["RuntimeVersion"].ToString().Trim(),
        CoreIdRangeFrom: int.TryParse(form["CoreIdRangeFrom"], out var cf) ? cf : 0,
        CoreIdRangeTo: int.TryParse(form["CoreIdRangeTo"], out var ctn) ? ctn : 0,
        IncludeExamples: form["IncludeExamples"] == "true" || form["IncludeExamples"] == "on",
        IncludeForNav: form["IncludeForNav"] == "true" || form["IncludeForNav"] == "on",
        SelectedModuleKeys: form["SelectedModuleKeys"]
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToList());

    try
    {
        var archive = await gen.GenerateWorkspaceAsync(plan, ct);
        ctx.Response.ContentType = "application/zip";
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{archive.FileName}\"";
        archive.Stream.Position = 0;
        await archive.Stream.CopyToAsync(ctx.Response.Body, ct);
    }
    catch (PlanValidationException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        var body = "The submitted form failed validation:\n\n"
            + string.Join("\n", ex.Errors.Select(e => $"  - {e.Key}: {e.Value}"));
        await ctx.Response.WriteAsync(body, ct);
    }
});

// File download endpoint for the New Extension flow. Same shape as the
// workspace endpoint above but maps the form to a StandaloneExtensionPlan and
// reconstructs the dependency list from four parallel hidden-input arrays.
app.MapPost("/generate/extension", async (HttpContext ctx, GenerationService gen, IAntiforgery antiforgery, CancellationToken ct) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(ctx);
    }
    catch (AntiforgeryValidationException)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        await ctx.Response.WriteAsync("Antiforgery validation failed. Reload the form and try again.", ct);
        return;
    }
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

    try
    {
        var archive = await gen.GenerateExtensionAsync(plan, ct);
        ctx.Response.ContentType = "application/zip";
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{archive.FileName}\"";
        archive.Stream.Position = 0;
        await archive.Stream.CopyToAsync(ctx.Response.Body, ct);
    }
    catch (PlanValidationException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        var body = "The submitted form failed validation:\n\n"
            + string.Join("\n", ex.Errors.Select(e => $"  - {e.Key}: {e.Value}"));
        await ctx.Response.WriteAsync(body, ct);
    }
});

// Run migrations (creating the database if needed) and then seed from disk if
// the templates table is empty. Both steps are idempotent on subsequent starts.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();

    var seed = scope.ServiceProvider.GetRequiredService<SeedService>();
    await seed.RunAsync();
}

app.Run();
