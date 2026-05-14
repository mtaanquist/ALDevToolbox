using ALDevToolbox.Components;
using ALDevToolbox.Data;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

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
// (Traefik / nginx / Caddy). See <c>.design/auth-and-audit.md</c>.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Cookie auth — Milestone P3.13 replaces the single shared password with
// real accounts. The cookie carries user_id, org_id and the user's role as
// claims; <c>HttpOrganizationContext</c> reads them to scope EF queries.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.Cookie.Name = "alwb_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";

        // /site-admin/* must return 404, never redirect-to-login or 403 —
        // a 403 would tell an org admin those routes exist. Both events
        // short-circuit to 404; status-code re-execute renders the
        // NotFound page.
        options.Events.OnRedirectToLogin = NotFoundForSiteAdmin;
        options.Events.OnRedirectToAccessDenied = NotFoundForSiteAdmin;

        static Task NotFoundForSiteAdmin(Microsoft.AspNetCore.Authentication.RedirectContext<CookieAuthenticationOptions> ctx)
        {
            if (ctx.Request.Path.StartsWithSegments(HttpOrganizationContext.SiteAdminPathPrefix))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }
            ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        }
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IOrganizationContext, HttpOrganizationContext>();

// Postgres connection string (M16). `ConnectionStrings__DefaultConnection`
// is the deployment knob; compose.yml builds it from POSTGRES_* env vars and
// passes it through. There is no fallback DSN — failing fast surfaces a
// missing config sooner than discovering it at first query.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection is not configured. Set ConnectionStrings__DefaultConnection.");
builder.Services.AddScoped<AuditInterceptor>();
// The model snapshot stays a hand-rolled affair (the InitialCreate designer
// file's BuildTargetModel is intentionally empty), so EF's pending-model-
// changes guard would fire on every MigrateAsync. Real schema drift still
// surfaces when the migration itself runs.
builder.Services.AddDbContext<AppDbContext>((sp, options) =>
    options
        .UseNpgsql(connectionString, npgsql => npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));

builder.Services.AddScoped<TemplateService>();
builder.Services.AddScoped<SnippetService>();
builder.Services.AddScoped<SnippetSuggestionService>();
builder.Services.AddScoped<ModuleService>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<ApplicationVersionService>();
builder.Services.AddScoped<BaseAppService>();
builder.Services.AddScoped<BaseAppImportService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<TemplateImportService>();
builder.Services.AddScoped<WorkspaceConfigService>();
builder.Services.AddSingleton<ALDevToolbox.Services.Generation.MustacheRenderer>();
builder.Services.AddScoped<ALDevToolbox.Services.Generation.WorkspaceZipBuilder>();
builder.Services.AddScoped<GenerationService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<OrganizationConfigService>();
builder.Services.AddScoped<ALDevToolbox.Services.Account.AuthService>();
builder.Services.AddScoped<ALDevToolbox.Services.Account.UserAdministrationService>();
builder.Services.AddScoped<ALDevToolbox.Services.Account.PasswordResetService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<InviteService>();
builder.Services.AddScoped<SystemSettingsService>();
builder.Services.AddScoped<SiteAdminService>();
builder.Services.AddScoped<BackupService>();
// MaintenanceModeState is a process-local flag — singleton lifetime so the
// middleware and BackupService share the same instance.
builder.Services.AddSingleton<MaintenanceModeState>();
// IconCatalog reads the vendored Lucide SVGs from embedded resources once
// at startup; singleton so we pay the parse cost a single time per process.
builder.Services.AddSingleton<IconCatalog>();
// The scheduler runs in the background; opt-out via DISABLE_BACKUP_SCHEDULER=1
// for environments (tests, CI) that don't want a background timer to start
// chasing pg_dump.
if (Environment.GetEnvironmentVariable("DISABLE_BACKUP_SCHEDULER") != "1")
{
    builder.Services.AddHostedService<BackupScheduler>();
}
// SymbolReindexer backfills the Object Explorer symbol index for versions
// imported before the symbol feature shipped. Same opt-out pattern as the
// backup scheduler so CI / tests can skip it. The queue is a singleton
// because the admin endpoint and the hosted service share the same instance
// — clicking "Reindex now" signals the worker immediately instead of
// waiting for the next 5-minute poll.
builder.Services.AddSingleton<SymbolReindexQueue>();
if (Environment.GetEnvironmentVariable("DISABLE_SYMBOL_REINDEXER") != "1")
{
    builder.Services.AddHostedService<SymbolReindexer>();
}
// Email shares the AppDbContext lifetime (Scoped) so it can read the
// hybrid SMTP override from system_settings.
builder.Services.AddScoped<IEmailService, SmtpEmailService>();

// Data Protection key ring. Persisted under DATA_PROTECTION_KEY_DIR
// (compose mounts the `app-keys` volume there) so cookie auth and the
// system_settings SMTP password ciphertext both survive container
// restarts. If the directory isn't writable we keep going with an
// in-memory key ring rather than crashing — operators see the warning
// in the startup logs and can fix the volume mount.
var dpKeyDir = Environment.GetEnvironmentVariable("DATA_PROTECTION_KEY_DIR")
    ?? "/var/lib/aldevtoolbox/dp-keys";
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("ALDevToolbox");
try
{
    Directory.CreateDirectory(dpKeyDir);
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dpKeyDir));
}
catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
{
    // Surfaces in startup logs; the cookie ring still works (in-memory),
    // but cookies and SMTP ciphertext won't survive a restart. Operators
    // see this immediately and can fix the volume mount.
    Console.Error.WriteLine($"WARN: Data Protection key dir '{dpKeyDir}' not writable ({ex.Message}). Keys will not persist.");
}

// Health checks (M21). /healthz is the live probe — green when the database
// is reachable and the Data Protection key ring round-trips. /readyz is
// distinct: it stays red until startup work (migrations + seed) has finished,
// so reverse proxies don't send traffic mid-migration.
builder.Services.AddSingleton<StartupReadinessState>();
builder.Services.AddScoped<DatabaseHealthCheck>();
builder.Services.AddSingleton<DataProtectionHealthCheck>();
builder.Services.AddSingleton<StartupReadinessHealthCheck>();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: new[] { "healthz" })
    .AddCheck<DataProtectionHealthCheck>("data-protection", tags: new[] { "healthz" })
    .AddCheck<StartupReadinessHealthCheck>("startup", tags: new[] { "readyz" });

var app = builder.Build();

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

// Maintenance mode (M18). While BackupService.RestoreAsync is mid-flight,
// every non-SiteAdmin request gets 503 + a tiny static body. SiteAdmin
// requests still go through so the operator can watch the restore page.
// Health-check endpoints also remain reachable so reverse proxies don't
// flap the container during a long restore.
app.Use(async (ctx, next) =>
{
    var maintenance = ctx.RequestServices.GetRequiredService<MaintenanceModeState>();
    if (!maintenance.IsActive)
    {
        await next();
        return;
    }
    var path = ctx.Request.Path;
    if (path.StartsWithSegments("/healthz")
        || path.StartsWithSegments("/readyz")
        || path.StartsWithSegments("/site-admin"))
    {
        await next();
        return;
    }
    if (ctx.User?.FindFirst(HttpOrganizationContext.SiteAdminClaim)?.Value == "true")
    {
        await next();
        return;
    }
    ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
    ctx.Response.Headers.RetryAfter = "30";
    ctx.Response.ContentType = "text/html; charset=utf-8";
    await ctx.Response.WriteAsync(
        $"<!doctype html><html><head><title>Maintenance · AL Dev Toolbox</title></head>"
        + $"<body style=\"font-family: system-ui, sans-serif; padding: 2rem;\">"
        + $"<h1>Maintenance in progress</h1>"
        + $"<p>{System.Net.WebUtility.HtmlEncode(maintenance.Reason ?? "The application is restoring from a backup.")}</p>"
        + $"<p>Started: {maintenance.StartedAtUtc:yyyy-MM-dd HH:mm 'UTC'}. The service will return shortly.</p>"
        + $"</body></html>");
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// /healthz: liveness probe — green when the database is reachable and the
// Data Protection key ring round-trips. /readyz: readiness probe — only
// green once startup work (migrations + seed) has finished.
app.MapHealthChecks("/healthz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("healthz"),
});
app.MapHealthChecks("/readyz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("readyz"),
});

// Endpoint groups (see Endpoints/ — one extension per concern).
app.MapGenerationEndpoints();
app.MapAdminEndpoints();
app.MapAccountEndpoints();
app.MapAdminUserEndpoints();
app.MapBaseAppEndpoints();
app.MapSiteAdminEndpoints();

// Run migrations + bootstrap, then flip /readyz to green.
await StartupTasks.RunAsync(app);

app.Run();

public partial class Program { }
