using System.Security.Claims;
using ALDevToolbox.Components;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
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

// Forwarded-headers — production runs behind a TLS-terminating proxy
// (Traefik / nginx / Caddy). Without this, Request.IsHttps is false and
// the auth cookie's Secure flag would never engage. Limiting the trusted
// proxy set is a follow-up for a hardened deployment; for now we accept
// X-Forwarded-* from any source on the bridge network.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Cookie auth — single shared admin password, no user accounts.
// See .design/auth-and-audit.md. The cookie stores the display name as a
// single claim; possession of a valid cookie means "this user is an admin".
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.Cookie.Name = "alwb_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        // The deployment doc requires Always — production runs behind a TLS
        // proxy; UseForwardedHeaders below makes Request.IsHttps reflect the
        // edge, so the cookie reliably gets the Secure flag. Local HTTP dev
        // (dotnet run on http://localhost:5000) won't issue a cookie until
        // ASPNETCORE_URLS includes an https:// binding — that's intentional.
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<AuthService>();

// SQLite connection string. DB_PATH wins, falling back to a file alongside the
// content root for local `dotnet run` so devs don't need to set anything up.
var dbPath = Environment.GetEnvironmentVariable("DB_PATH")
    ?? Path.Combine(builder.Environment.ContentRootPath, "app.db");
builder.Services.AddScoped<AuditInterceptor>();
builder.Services.AddDbContext<AppDbContext>((sp, options) =>
    options
        .UseSqlite($"Data Source={dbPath}", sqlite => sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
        .AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));

builder.Services.AddScoped<TemplateService>();
builder.Services.AddScoped<ModuleService>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<SeedService>();
builder.Services.AddScoped<WorkspaceConfigService>();
builder.Services.AddScoped<GenerationService>();
builder.Services.AddScoped<ExportService>();

// Health checks. /health is a liveness probe (always 200 if the process is
// up); /health/ready exercises the database with a SELECT 1 and is what an
// orchestrator should poll before sending traffic. Tagged so we can route
// the same set of checks to both endpoints with different predicates.
builder.Services.AddScoped<DatabaseHealthCheck>();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: new[] { "ready" });

var app = builder.Build();

// Honour X-Forwarded-Proto / X-Forwarded-For from the upstream proxy. Must
// run before authentication so the cookie's Secure decision and any
// downstream URL building see the edge scheme, not the http://+:8080 the
// container actually listens on.
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Liveness: process is up. Readiness: database is reachable. Both are
// unauthenticated so an external load balancer or compose healthcheck can
// poll without credentials. See .design/deployment.md.
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});

// File download endpoint for the New Workspace flow. The form posts here and
// the response is a ZIP attachment. Validation errors render an inline error
// page so the user can navigate back; the form fields themselves enforce most
// of the same rules client-side via HTML attributes.
app.MapPost("/generate/workspace", async (HttpContext ctx, GenerationService gen, IAntiforgery antiforgery, CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
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
});

// File download endpoint for the New Extension flow. Same shape as the
// workspace endpoint above but maps the form to a StandaloneExtensionPlan and
// reconstructs the dependency list from four parallel hidden-input arrays.
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

    // Sibling-extension context: present when the page had a workspace config
    // imported. The hidden inputs survive the antiforgery + form post round
    // trip without a session, so the endpoint stays stateless. Folder names
    // come from the persisted identities so the regenerated .code-workspace
    // uses the names the user already has on disk.
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
});

// Admin: export the current database state as a TOML ZIP that mirrors the
// Templates.seed/ layout. Used as a one-click backup or for diffing the live
// state outside the app. See .design/templates-and-seeding.md.
app.MapPost("/admin/export", async (HttpContext ctx, ExportService export, IAntiforgery antiforgery, CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

    var archive = await export.ExportAllAsync(ct);
    WriteAttachmentHeaders(ctx, archive.FileName);
    archive.Stream.Position = 0;
    await archive.Stream.CopyToAsync(ctx.Response.Body, ct);
}).RequireAuthorization();

// Login endpoint: validates the submitted password in constant time, captures
// the honour-system display name, and issues the auth cookie. The Login page
// posts here directly so the sign-in happens during a real HTTP request (cookie
// SignIn doesn't work over the SignalR pipe). Failures redirect back to /login
// with an error code rather than telling the user *why* sign-in failed.
app.MapPost("/auth/login", async (HttpContext ctx, AuthService auth, IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
{
    var logger = loggerFactory.CreateLogger("Auth");
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

    var form = await ctx.Request.ReadFormAsync(ct);
    var password = form["Password"].ToString();
    var displayName = form["DisplayName"].ToString().Trim();
    var requestedReturn = form["ReturnUrl"].ToString();
    // Open-redirect guard: only allow same-site relative paths. Reject anything
    // that could resolve to a different host, including protocol-relative
    // forms ("//evil.com/foo").
    var safeReturn = !string.IsNullOrEmpty(requestedReturn)
                     && Uri.IsWellFormedUriString(requestedReturn, UriKind.Relative)
                     && requestedReturn.StartsWith('/')
                     && !requestedReturn.StartsWith("//", StringComparison.Ordinal)
                     && !requestedReturn.StartsWith("/\\", StringComparison.Ordinal)
        ? requestedReturn
        : "/admin";

    if (!auth.IsConfigured)
    {
        logger.LogWarning("Login attempted but no admin password is configured.");
        ctx.Response.Redirect("/login?err=not-configured");
        return;
    }

    if (string.IsNullOrEmpty(displayName) || !auth.Verify(password))
    {
        logger.LogInformation("Failed login attempt.");
        var qs = $"/login?err=invalid&return={Uri.EscapeDataString(safeReturn)}";
        ctx.Response.Redirect(qs);
        return;
    }

    var identity = new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.Name, displayName) },
        CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity));
    logger.LogInformation("Admin signed in as {DisplayName}.", displayName);
    ctx.Response.Redirect(safeReturn);
});

// Logout endpoint: clears the cookie and returns to the home page. Posted from
// the top-bar sign-out button, antiforgery-protected like every other mutation.
app.MapPost("/auth/logout", async (HttpContext ctx, IAntiforgery antiforgery, CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    ctx.Response.Redirect("/");
});

// Run migrations (creating the database if needed) and then seed from disk if
// the templates table is empty. Both steps are idempotent on subsequent starts.
// Honour the host's stop signal so a Ctrl-C during a slow first-run seed
// doesn't leave a half-populated database.
using (var scope = app.Services.CreateScope())
{
    var stopping = app.Lifetime.ApplicationStopping;
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync(stopping);

    var seed = scope.ServiceProvider.GetRequiredService<SeedService>();
    await seed.RunAsync(stopping);
}

app.Run();

// Writes Content-Type + a properly quoted Content-Disposition for a ZIP
// download. Routes filename through ContentDispositionHeaderValue so a
// hostile filename can't smuggle CR/LF or embedded quotes into the
// response headers — today the names are server-built, but doing it
// once here means future callers get the same treatment for free.
static void WriteAttachmentHeaders(HttpContext ctx, string fileName)
{
    ctx.Response.ContentType = "application/zip";
    var cd = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
    cd.SetHttpFileName(fileName);
    ctx.Response.Headers.ContentDisposition = cd.ToString();
}

// Antiforgery preamble shared by every mutating endpoint. Returns true when
// the token is valid; otherwise writes the 400 response inline and returns
// false so the caller can early-out without nesting another try/catch.
static async Task<bool> ValidateAntiforgeryAsync(HttpContext ctx, IAntiforgery antiforgery, CancellationToken ct)
{
    try
    {
        await antiforgery.ValidateRequestAsync(ctx);
        return true;
    }
    catch (AntiforgeryValidationException)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        await ctx.Response.WriteAsync("Antiforgery validation failed. Reload the form and try again.", ct);
        return false;
    }
}

// The generate.js companion polls this cookie to know when the matching
// download response has finished, so the Generate button can drop its
// loading state. Empty tokens are ignored — the form always includes a
// freshly-stamped value, but legacy bookmarks or curl callers won't.
static void SetGenerationCompleteCookie(HttpContext ctx, string token)
{
    if (string.IsNullOrEmpty(token)) return;
    ctx.Response.Cookies.Append("aldt-gen", token, new Microsoft.AspNetCore.Http.CookieOptions
    {
        HttpOnly = false,
        SameSite = SameSiteMode.Lax,
        Secure = ctx.Request.IsHttps,
        Path = "/",
        MaxAge = TimeSpan.FromSeconds(30),
    });
}
