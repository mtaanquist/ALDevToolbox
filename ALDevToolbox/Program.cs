using System.Security.Claims;
using ALDevToolbox.Components;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
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
builder.Services.AddScoped<ModuleService>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<ApplicationVersionService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<SeedService>();
builder.Services.AddScoped<WorkspaceConfigService>();
builder.Services.AddScoped<GenerationService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<OrganizationConfigService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<SystemSettingsService>();
builder.Services.AddScoped<SiteAdminService>();
builder.Services.AddScoped<BackupService>();
// MaintenanceModeState is a process-local flag — singleton lifetime so the
// middleware and BackupService share the same instance.
builder.Services.AddSingleton<MaintenanceModeState>();
// The scheduler runs in the background; opt-out via DISABLE_BACKUP_SCHEDULER=1
// for environments (tests, CI) that don't want a background timer to start
// chasing pg_dump.
if (Environment.GetEnvironmentVariable("DISABLE_BACKUP_SCHEDULER") != "1")
{
    builder.Services.AddHostedService<BackupScheduler>();
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

// Health checks. /health is a liveness probe; /health/ready exercises the database.
builder.Services.AddScoped<DatabaseHealthCheck>();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: new[] { "ready" });

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
    if (path.StartsWithSegments("/health") || path.StartsWithSegments("/site-admin"))
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

app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});

// File download endpoint for the New Workspace flow. Now requires a signed-in
// user — anonymous access to the generators stops with M13.
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

// /admin/configuration/* — endpoints behind the admin configuration page.
// Each section posts independently so the user can save Defaults, Logo and
// Files in isolation; failure on one section doesn't roll back the others.
app.MapPost("/admin/configuration/settings", async (
    HttpContext ctx, OrganizationConfigService config, IAntiforgery antiforgery, CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    var form = await ctx.Request.ReadFormAsync(ct);
    var input = new OrganizationSettingsInput(
        DefaultPublisher: form["DefaultPublisher"].ToString().Trim(),
        DefaultIdRangeFrom: int.TryParse(form["DefaultIdRangeFrom"], out var from) ? from : 0,
        DefaultIdRangeTo: int.TryParse(form["DefaultIdRangeTo"], out var to) ? to : 0,
        DefaultBrief: form["DefaultBrief"].ToString().Trim(),
        DefaultCoreDescription: form["DefaultCoreDescription"].ToString().Trim());
    try
    {
        await config.SaveSettingsAsync(input, ct);
        ctx.Response.Redirect("/admin/configuration?ok=settings");
    }
    catch (PlanValidationException ex)
    {
        var first = ex.Errors.First();
        ctx.Response.Redirect(
            $"/admin/configuration?err={Uri.EscapeDataString(first.Key)}&msg={Uri.EscapeDataString(first.Value)}");
    }
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPost("/admin/configuration/logo", async (
    HttpContext ctx, OrganizationConfigService config, IAntiforgery antiforgery, CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    var form = await ctx.Request.ReadFormAsync(ct);
    var file = form.Files["Logo"];
    if (file is null || file.Length == 0)
    {
        ctx.Response.Redirect("/admin/configuration?err=Logo&msg=" + Uri.EscapeDataString("Pick a logo file to upload."));
        return;
    }
    using var ms = new MemoryStream();
    await file.CopyToAsync(ms, ct);
    try
    {
        await config.UploadLogoAsync(file.ContentType ?? string.Empty, ms.ToArray(), ct);
        ctx.Response.Redirect("/admin/configuration?ok=logo");
    }
    catch (PlanValidationException ex)
    {
        var first = ex.Errors.First();
        ctx.Response.Redirect(
            $"/admin/configuration?err={Uri.EscapeDataString(first.Key)}&msg={Uri.EscapeDataString(first.Value)}");
    }
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPost("/admin/configuration/logo/revert", async (
    HttpContext ctx, OrganizationConfigService config, IAntiforgery antiforgery, CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    await config.RevertLogoAsync(ct);
    ctx.Response.Redirect("/admin/configuration?ok=logo-reverted");
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapGet("/admin/configuration/logo/preview", async (
    HttpContext ctx, OrganizationConfigService config, CancellationToken ct) =>
{
    var snapshot = await config.GetCurrentAsync(ct);
    if (snapshot.Logo is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    ctx.Response.ContentType = snapshot.Logo.ContentType;
    // Cache-busting handled by the page (it appends ?t=… to the URL after a save).
    ctx.Response.Headers.CacheControl = "no-store";
    await ctx.Response.Body.WriteAsync(snapshot.Logo.Content, ct);
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPost("/admin/configuration/import", async (
    HttpContext ctx, OrganizationConfigService config, IAntiforgery antiforgery, CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    var form = await ctx.Request.ReadFormAsync(ct);
    var toml = form["Toml"].ToString();
    var confirmed = form["Confirm"] == "true" || form["Confirm"] == "on";
    if (!confirmed)
    {
        ctx.Response.Redirect(
            "/admin/configuration?err=Confirm&msg=" + Uri.EscapeDataString("Tick the confirmation box before importing."));
        return;
    }
    try
    {
        await config.ImportFromTomlAsync(toml, ct);
        ctx.Response.Redirect("/admin/configuration?ok=imported");
    }
    catch (PlanValidationException ex)
    {
        var first = ex.Errors.First();
        ctx.Response.Redirect(
            $"/admin/configuration?err={Uri.EscapeDataString(first.Key)}&msg={Uri.EscapeDataString(first.Value)}");
    }
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPost("/admin/export", async (HttpContext ctx, ExportService export, IAntiforgery antiforgery, CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

    var archive = await export.ExportAllAsync(ct);
    WriteAttachmentHeaders(ctx, archive.FileName);
    archive.Stream.Position = 0;
    await archive.Stream.CopyToAsync(ctx.Response.Body, ct);
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

// /auth/login: validates the email + password, sets the auth cookie with the
// user_id / org_id / role / email claims, and triggers seeding for orgs
// being touched by their first admin login.
app.MapPost("/auth/login", async (
    HttpContext ctx,
    AccountService accounts,
    AppDbContext db,
    SeedService seed,
    IAntiforgery antiforgery,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var logger = loggerFactory.CreateLogger("Auth");
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

    var form = await ctx.Request.ReadFormAsync(ct);
    var email = form["Email"].ToString();
    var password = form["Password"].ToString();
    var requestedReturn = form["ReturnUrl"].ToString();
    var safeReturn = ResolveSafeReturn(requestedReturn);
    var ip = ResolveIp(ctx);

    var (outcome, user) = await accounts.TryLoginAsync(email, password, ip, ct);
    if (outcome != LoginOutcome.Success || user is null)
    {
        var code = outcome switch
        {
            LoginOutcome.Pending => "pending",
            LoginOutcome.Disabled => "disabled",
            LoginOutcome.LockedOut => "locked",
            LoginOutcome.RateLimited => "rate-limited",
            _ => "invalid",
        };
        logger.LogInformation("Login attempt for {Email} from {Ip} resolved {Outcome}.", email, ip, outcome);
        ctx.Response.Redirect($"/login?err={code}&return={Uri.EscapeDataString(safeReturn)}");
        return;
    }

    // First-admin-login seeding for orgs that were created via signup. We
    // skip for the Default org which the migration stamps as already seeded.
    if (user.Role == UserRole.Admin)
    {
        var org = await db.Organizations.IgnoreQueryFilters().FirstAsync(o => o.Id == user.OrganizationId, ct);
        if (!org.IsSeeded)
        {
            logger.LogInformation("First admin login for org {OrgId}; running seed.", org.Id);
            await seed.RunAsync(org.Id, ct);
            org.IsSeeded = true;
            await db.SaveChangesAsync(ct);
        }
    }

    var identity = BuildIdentity(user);
    await ctx.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity));
    logger.LogInformation("Signed in {Email} (org {OrgId}, role {Role}).", user.Email, user.OrganizationId, user.Role);
    ctx.Response.Redirect(safeReturn);
});

// /auth/logout — clears the cookie. Posted from the top-bar sign-out button.
app.MapPost("/auth/logout", async (HttpContext ctx, IAntiforgery antiforgery, CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    ctx.Response.Redirect("/");
});

// /auth/signup — two paths:
//  - Existing-org signup: creates a Pending user, emails the org's active
//    admins, redirects with a "queued" message.
//  - New-org signup: auto-approves (we have no superuser to do otherwise),
//    seeds the org from Templates.seed/, signs the user in, and lands them
//    on the home page as the new org's admin.
// Email send failures log a warning but never roll back the signup.
app.MapPost("/auth/signup", async (
    HttpContext ctx,
    AccountService accounts,
    AppDbContext db,
    SeedService seed,
    IEmailService email,
    IAntiforgery antiforgery,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var logger = loggerFactory.CreateLogger("Signup");
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    var form = await ctx.Request.ReadFormAsync(ct);
    try
    {
        var (outcome, user, org) = await accounts.SignupAsync(
            email: form["Email"].ToString(),
            displayName: form["DisplayName"].ToString(),
            password: form["Password"].ToString(),
            organizationSlug: form["OrganizationSlug"].ToString(),
            ct);

        if (outcome == SignupOutcome.EmailAlreadyTaken)
        {
            ctx.Response.Redirect("/signup?err=email-taken");
            return;
        }

        if (outcome == SignupOutcome.OrganizationProvisioned && user is not null && org is not null)
        {
            // Auto-approved: seed the new org's content and sign the user in
            // so they can use the generator immediately. Re-load the user
            // with its Organization navigation populated so the cookie
            // claims include the org name for the top bar.
            if (!org.IsSeeded)
            {
                await seed.RunAsync(org.Id, ct);
                org.IsSeeded = true;
                await db.SaveChangesAsync(ct);
            }
            user.Organization = org;
            await ctx.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(BuildIdentity(user)));
            logger.LogInformation("Auto-approved new-org signup {Email} as admin of {OrgSlug}.", user.Email, org.Slug);
            ctx.Response.Redirect("/");
            return;
        }

        // Existing-org signup: notify the org's active admins so they can
        // approve via /admin/users. SMTP failures don't roll back the signup.
        if (org is not null && await email.IsConfiguredAsync(ct))
        {
            try
            {
                var admins = await db.Users.IgnoreQueryFilters()
                    .Where(u => u.OrganizationId == org.Id
                                && u.Role == UserRole.Admin
                                && u.Status == UserStatus.Active)
                    .ToListAsync(ct);
                foreach (var admin in admins)
                {
                    var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/admin/users";
                    var (subject, body) = EmailTemplates.SignupPending(admin.DisplayName, user!.Email, org.Name, url);
                    await email.SendAsync(admin.Email, subject, body, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to email admins about pending signup {Email}.", user?.Email);
            }
        }

        ctx.Response.Redirect("/signup?ok=pending");
    }
    catch (PlanValidationException ex)
    {
        var qs = ex.Errors.FirstOrDefault();
        ctx.Response.Redirect($"/signup?err=invalid&field={Uri.EscapeDataString(qs.Key)}&msg={Uri.EscapeDataString(qs.Value)}");
    }
});

// /auth/forgot-password — same response regardless of email existence.
app.MapPost("/auth/forgot-password", async (
    HttpContext ctx,
    AccountService accounts,
    AppDbContext db,
    IEmailService email,
    IAntiforgery antiforgery,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var logger = loggerFactory.CreateLogger("ForgotPassword");
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    if (!await email.IsConfiguredAsync(ct))
    {
        ctx.Response.Redirect("/forgot-password?err=not-configured");
        return;
    }
    var form = await ctx.Request.ReadFormAsync(ct);
    var addr = form["Email"].ToString();
    try
    {
        var token = await accounts.CreatePasswordResetTokenAsync(addr, ct);
        if (token is not null)
        {
            var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Email == addr.Trim().ToLowerInvariant(), ct);
            var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/reset-password?token={Uri.EscapeDataString(token)}";
            var (subject, body) = EmailTemplates.ForgotPassword(user.DisplayName, url);
            await email.SendAsync(user.Email, subject, body, ct);
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Forgot-password flow failed for {Email}.", addr);
    }
    ctx.Response.Redirect("/forgot-password?ok=1");
});

// /auth/reset-password — consumes the token and applies the new password.
app.MapPost("/auth/reset-password", async (
    HttpContext ctx,
    AccountService accounts,
    IAntiforgery antiforgery,
    CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    var form = await ctx.Request.ReadFormAsync(ct);
    var token = form["Token"].ToString();
    var password = form["Password"].ToString();
    try
    {
        await accounts.ConsumePasswordResetTokenAsync(token, password, ct);
        ctx.Response.Redirect("/login?ok=password-reset");
    }
    catch (PlanValidationException ex)
    {
        var first = ex.Errors.FirstOrDefault();
        ctx.Response.Redirect(
            $"/reset-password?token={Uri.EscapeDataString(token)}&err={Uri.EscapeDataString(first.Key)}&msg={Uri.EscapeDataString(first.Value)}");
    }
});

// /auth/account/* — self-service. All require [Authorize].
app.MapPost("/auth/account/password", async (
    HttpContext ctx, AccountService accounts, IOrganizationContext org, IAntiforgery antiforgery, CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    var form = await ctx.Request.ReadFormAsync(ct);
    try
    {
        await accounts.ChangePasswordAsync(org.CurrentUserId!.Value,
            form["CurrentPassword"].ToString(), form["NewPassword"].ToString(), ct);
        ctx.Response.Redirect("/account?ok=password");
    }
    catch (PlanValidationException ex)
    {
        var first = ex.Errors.FirstOrDefault();
        ctx.Response.Redirect($"/account?err={Uri.EscapeDataString(first.Key)}&msg={Uri.EscapeDataString(first.Value)}");
    }
}).RequireAuthorization();

app.MapPost("/auth/account/display-name", async (
    HttpContext ctx, AccountService accounts, IOrganizationContext org, IAntiforgery antiforgery, CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    var form = await ctx.Request.ReadFormAsync(ct);
    try
    {
        await accounts.ChangeDisplayNameAsync(org.CurrentUserId!.Value, form["DisplayName"].ToString(), ct);
        ctx.Response.Redirect("/account?ok=display-name");
    }
    catch (PlanValidationException ex)
    {
        var first = ex.Errors.FirstOrDefault();
        ctx.Response.Redirect($"/account?err={Uri.EscapeDataString(first.Key)}&msg={Uri.EscapeDataString(first.Value)}");
    }
}).RequireAuthorization();

app.MapPost("/auth/account/delete", async (
    HttpContext ctx, AccountService accounts, IOrganizationContext org, IAntiforgery antiforgery, CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    var form = await ctx.Request.ReadFormAsync(ct);
    var accept = form["AcceptOrgDeletion"] == "true" || form["AcceptOrgDeletion"] == "on";
    try
    {
        await accounts.DeleteAccountAsync(org.CurrentUserId!.Value, accept, ct);
        await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        ctx.Response.Redirect("/login?ok=account-deleted");
    }
    catch (PlanValidationException ex)
    {
        var first = ex.Errors.FirstOrDefault();
        ctx.Response.Redirect($"/account?err={Uri.EscapeDataString(first.Key)}&msg={Uri.EscapeDataString(first.Value)}");
    }
}).RequireAuthorization();

// /admin/users/* — approve / reject / disable / role change. Admin-only.
app.MapPost("/admin/users/{id:int}/approve", async (
    int id, HttpContext ctx, AccountService accounts, AppDbContext db, IEmailService email,
    IOrganizationContext org, IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
{
    var logger = loggerFactory.CreateLogger("AdminUsers");
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    await accounts.ApproveSignupAsync(id, org.CurrentUserId!.Value, org.CurrentOrganizationId!.Value, ct);
    if (await email.IsConfiguredAsync(ct))
    {
        try
        {
            var req = await db.SignupRequests.IgnoreQueryFilters()
                .Include(r => r.User).Include(r => r.Organization)
                .FirstAsync(r => r.Id == id, ct);
            if (req.User is not null && req.Organization is not null)
            {
                var loginUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/login";
                var (subject, body) = EmailTemplates.SignupDecided(
                    req.User.DisplayName, req.Organization.Name, approved: true, loginUrl);
                await email.SendAsync(req.User.Email, subject, body, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Approval email failed for signup {Id}.", id);
        }
    }
    ctx.Response.Redirect("/admin/users");
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPost("/admin/users/{id:int}/reject", async (
    int id, HttpContext ctx, AccountService accounts, AppDbContext db, IEmailService email,
    IOrganizationContext org, IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
{
    var logger = loggerFactory.CreateLogger("AdminUsers");
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    var req = await db.SignupRequests.IgnoreQueryFilters()
        .Include(r => r.User).Include(r => r.Organization)
        .FirstOrDefaultAsync(r => r.Id == id, ct);
    var requesterEmail = req?.User?.Email;
    var requesterDisplay = req?.User?.DisplayName ?? "User";
    var orgName = req?.Organization?.Name ?? "Unknown organisation";

    await accounts.RejectSignupAsync(id, org.CurrentUserId!.Value, org.CurrentOrganizationId!.Value, ct);

    if (await email.IsConfiguredAsync(ct) && !string.IsNullOrEmpty(requesterEmail))
    {
        try
        {
            var loginUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/login";
            var (subject, body) = EmailTemplates.SignupDecided(requesterDisplay, orgName, approved: false, loginUrl);
            await email.SendAsync(requesterEmail, subject, body, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Rejection email failed for signup {Id}.", id);
        }
    }
    ctx.Response.Redirect("/admin/users");
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPost("/admin/users/{id:int}/disable", async (
    int id, HttpContext ctx, AccountService accounts, IOrganizationContext org, IAntiforgery antiforgery, CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    try { await accounts.DisableUserAsync(id, org.CurrentOrganizationId!.Value, ct); }
    catch (PlanValidationException ex)
    {
        var first = ex.Errors.FirstOrDefault();
        ctx.Response.Redirect($"/admin/users?err={Uri.EscapeDataString(first.Value)}");
        return;
    }
    ctx.Response.Redirect("/admin/users");
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPost("/admin/users/{id:int}/enable", async (
    int id, HttpContext ctx, AccountService accounts, IOrganizationContext org, IAntiforgery antiforgery, CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    await accounts.EnableUserAsync(id, org.CurrentOrganizationId!.Value, ct);
    ctx.Response.Redirect("/admin/users");
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPost("/admin/users/{id:int}/role", async (
    int id, HttpContext ctx, AccountService accounts, IOrganizationContext org, IAntiforgery antiforgery, CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    var form = await ctx.Request.ReadFormAsync(ct);
    var newRole = form["Role"].ToString() == "Admin" ? UserRole.Admin : UserRole.User;
    try { await accounts.ChangeRoleAsync(id, newRole, org.CurrentOrganizationId!.Value, ct); }
    catch (PlanValidationException ex)
    {
        var first = ex.Errors.FirstOrDefault();
        ctx.Response.Redirect($"/admin/users?err={Uri.EscapeDataString(first.Value)}");
        return;
    }
    ctx.Response.Redirect("/admin/users");
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

// /site-admin/* — hosting-operator endpoints. The cookie events
// translate auth failures on this prefix into 404 so non-SiteAdmins
// can't even confirm the routes exist.
app.MapPost("/site-admin/users/{id:int}/promote", async (
    int id, HttpContext ctx, SiteAdminService siteAdmin, IAntiforgery antiforgery, CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    try { await siteAdmin.PromoteAsync(id, ct); }
    catch (PlanValidationException ex)
    {
        var first = ex.Errors.FirstOrDefault();
        ctx.Response.Redirect($"/site-admin/users?err={Uri.EscapeDataString(first.Value)}");
        return;
    }
    ctx.Response.Redirect("/site-admin/users?ok=promoted");
}).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

app.MapPost("/site-admin/users/{id:int}/demote", async (
    int id, HttpContext ctx, SiteAdminService siteAdmin, IAntiforgery antiforgery, CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    try { await siteAdmin.DemoteAsync(id, ct); }
    catch (PlanValidationException ex)
    {
        var first = ex.Errors.FirstOrDefault();
        ctx.Response.Redirect($"/site-admin/users?err={Uri.EscapeDataString(first.Value)}");
        return;
    }
    ctx.Response.Redirect("/site-admin/users?ok=demoted");
}).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

app.MapPost("/site-admin/settings", async (
    HttpContext ctx, SystemSettingsService settings, IAntiforgery antiforgery, CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    var form = await ctx.Request.ReadFormAsync(ct);
    var input = new SystemSettingsInput(
        SmtpHost: form["SmtpHost"].ToString(),
        SmtpPort: int.TryParse(form["SmtpPort"], out var port) ? port : null,
        SmtpUser: form["SmtpUser"].ToString(),
        SmtpPassword: form["SmtpPassword"].ToString(),
        ClearSmtpPassword: form["ClearSmtpPassword"] == "true" || form["ClearSmtpPassword"] == "on",
        SmtpFrom: form["SmtpFrom"].ToString(),
        SmtpUseStartTls: form.ContainsKey("SmtpUseStartTls")
            ? (form["SmtpUseStartTls"] == "true" || form["SmtpUseStartTls"] == "on")
            : null,
        BannerText: form["BannerText"].ToString(),
        DefaultSignupAutoApprove: form["DefaultSignupAutoApprove"] == "true" || form["DefaultSignupAutoApprove"] == "on",
        BackupScheduleEnabled: form["BackupScheduleEnabled"] == "true" || form["BackupScheduleEnabled"] == "on",
        BackupScheduleTimeUtc: TimeOnly.TryParse(form["BackupScheduleTimeUtc"], out var bst) ? bst : new TimeOnly(2, 0),
        BackupRetentionCount: int.TryParse(form["BackupRetentionCount"], out var brc) ? brc : 14);

    try
    {
        await settings.SaveAsync(input, ct);
        ctx.Response.Redirect("/site-admin/settings?ok=saved");
    }
    catch (PlanValidationException ex)
    {
        var first = ex.Errors.First();
        ctx.Response.Redirect("/site-admin/settings?msg=" + Uri.EscapeDataString(first.Value));
    }
}).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

app.MapPost("/site-admin/settings/test-email", async (
    HttpContext ctx, IEmailService email, AppDbContext db, IOrganizationContext orgCtx,
    IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
{
    var logger = loggerFactory.CreateLogger("SiteAdminTestEmail");
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    if (!await email.IsConfiguredAsync(ct))
    {
        ctx.Response.Redirect("/site-admin/settings?msg=" + Uri.EscapeDataString("SMTP is not configured."));
        return;
    }
    var userId = orgCtx.CurrentUserId;
    if (userId is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    var recipient = await db.Users.AsNoTracking()
        .Where(u => u.Id == userId.Value)
        .Select(u => new { u.Email, u.DisplayName })
        .FirstAsync(ct);
    try
    {
        var (subject, body) = EmailTemplates.SiteAdminTest(recipient.DisplayName);
        await email.SendAsync(recipient.Email, subject, body, ct);
        ctx.Response.Redirect("/site-admin/settings?ok=test-sent");
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Test email failed for SiteAdmin {Email}.", recipient.Email);
        ctx.Response.Redirect("/site-admin/settings?msg="
            + Uri.EscapeDataString("Test email failed: " + ex.Message));
    }
}).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

// /site-admin/backups/* — backup tooling (M18). Ad-hoc create, pin/unpin,
// delete, download, and the destructive in-place restore.
app.MapPost("/site-admin/backups/create", async (
    HttpContext ctx, BackupService backups, IAntiforgery antiforgery, CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    try
    {
        await backups.CreateAsync(BackupKind.AdHoc, ct);
        ctx.Response.Redirect("/site-admin/backups?ok=created");
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException)
    {
        ctx.Response.Redirect("/site-admin/backups?msg=" + Uri.EscapeDataString(ex.Message));
    }
}).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

app.MapPost("/site-admin/backups/{id:int}/pin", async (
    int id, HttpContext ctx, BackupService backups, IAntiforgery antiforgery, CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    try { await backups.SetPinnedAsync(id, pinned: true, ct); }
    catch (PlanValidationException ex)
    {
        ctx.Response.Redirect("/site-admin/backups?msg=" + Uri.EscapeDataString(ex.Errors.First().Value));
        return;
    }
    ctx.Response.Redirect("/site-admin/backups?ok=pinned");
}).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

app.MapPost("/site-admin/backups/{id:int}/unpin", async (
    int id, HttpContext ctx, BackupService backups, IAntiforgery antiforgery, CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    try { await backups.SetPinnedAsync(id, pinned: false, ct); }
    catch (PlanValidationException ex)
    {
        ctx.Response.Redirect("/site-admin/backups?msg=" + Uri.EscapeDataString(ex.Errors.First().Value));
        return;
    }
    ctx.Response.Redirect("/site-admin/backups?ok=unpinned");
}).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

app.MapPost("/site-admin/backups/{id:int}/delete", async (
    int id, HttpContext ctx, BackupService backups, IAntiforgery antiforgery, CancellationToken ct) =>
{
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    try { await backups.DeleteAsync(id, ct); }
    catch (PlanValidationException ex)
    {
        ctx.Response.Redirect("/site-admin/backups?msg=" + Uri.EscapeDataString(ex.Errors.First().Value));
        return;
    }
    ctx.Response.Redirect("/site-admin/backups?ok=deleted");
}).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

app.MapPost("/site-admin/backups/{id:int}/restore", async (
    int id, HttpContext ctx, BackupService backups, IAntiforgery antiforgery,
    ILoggerFactory loggerFactory, CancellationToken ct) =>
{
    var logger = loggerFactory.CreateLogger("RestoreEndpoint");
    if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
    var form = await ctx.Request.ReadFormAsync(ct);
    var confirmed = form["Confirm"] == "true" || form["Confirm"] == "on";
    if (!confirmed)
    {
        ctx.Response.Redirect("/site-admin/backups?msg="
            + Uri.EscapeDataString("Tick the confirmation box before restoring."));
        return;
    }
    try
    {
        await backups.RestoreAsync(id, ct);
        ctx.Response.Redirect("/site-admin/backups?ok=restored");
    }
    catch (PlanValidationException ex)
    {
        ctx.Response.Redirect("/site-admin/backups?msg=" + Uri.EscapeDataString(ex.Errors.First().Value));
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException)
    {
        logger.LogError(ex, "Restore failed for backup {Id}.", id);
        ctx.Response.Redirect("/site-admin/backups?msg=" + Uri.EscapeDataString("Restore failed: " + ex.Message));
    }
}).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

app.MapGet("/site-admin/backups/{id:int}/download", async (
    int id, HttpContext ctx, BackupService backups, CancellationToken ct) =>
{
    var opened = await backups.OpenForDownloadAsync(id, ct);
    if (opened is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    var (row, stream) = opened.Value;
    try
    {
        ctx.Response.ContentType = "application/octet-stream";
        var cd = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
        cd.SetHttpFileName(row.FileName);
        ctx.Response.Headers.ContentDisposition = cd.ToString();
        ctx.Response.ContentLength = row.FileSizeBytes;
        await stream.CopyToAsync(ctx.Response.Body, ct);
    }
    finally
    {
        await stream.DisposeAsync();
    }
}).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

// Run migrations, then bootstrap the admin account from BOOTSTRAP_ADMIN_*
// env vars on first boot. The Default organisation is created by the M13
// migration; SeedService runs against it only if its content is missing
// (post-migration the Default org is already populated and IsSeeded=true).
using (var scope = app.Services.CreateScope())
{
    var stopping = app.Lifetime.ApplicationStopping;
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var seed = scope.ServiceProvider.GetRequiredService<SeedService>();
    var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
    var bootstrapLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await db.Database.MigrateAsync(stopping);

    // Ensure the Default org exists (covers EnsureCreated paths in tests).
    var defaultOrg = await db.Organizations.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.Slug == "default", stopping);
    if (defaultOrg is null)
    {
        defaultOrg = new Organization
        {
            Name = "Default",
            Slug = "default",
            IsPending = false,
            IsSeeded = false,
            CreatedAt = DateTime.UtcNow,
        };
        db.Organizations.Add(defaultOrg);
        await db.SaveChangesAsync(stopping);
    }

    if (!defaultOrg.IsSeeded)
    {
        await seed.RunAsync(defaultOrg.Id, stopping);
        defaultOrg.IsSeeded = true;
        await db.SaveChangesAsync(stopping);
    }

    // Bootstrap admin: only runs once, when there are no users in the
    // database. After that the env vars are read but ignored (logged).
    var bootstrapEmail = Environment.GetEnvironmentVariable("BOOTSTRAP_ADMIN_EMAIL");
    var bootstrapPassword = Environment.GetEnvironmentVariable("BOOTSTRAP_ADMIN_PASSWORD");
    var anyUsers = await db.Users.IgnoreQueryFilters().AnyAsync(stopping);
    if (!anyUsers && !string.IsNullOrWhiteSpace(bootstrapEmail) && !string.IsNullOrWhiteSpace(bootstrapPassword))
    {
        var hash = accounts.HashPassword(bootstrapPassword);
        db.Users.Add(new User
        {
            OrganizationId = defaultOrg.Id,
            Email = bootstrapEmail.Trim().ToLowerInvariant(),
            DisplayName = "Administrator",
            PasswordHash = hash,
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            // The bootstrap admin is the first hosting operator;
            // subsequent SiteAdmins are promoted from /site-admin/users.
            IsSiteAdmin = true,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(stopping);
        bootstrapLogger.LogInformation("Bootstrap SiteAdmin {Email} created in Default organisation.", bootstrapEmail);
    }
    else if (anyUsers && (!string.IsNullOrWhiteSpace(bootstrapEmail) || !string.IsNullOrWhiteSpace(bootstrapPassword)))
    {
        bootstrapLogger.LogWarning(
            "BOOTSTRAP_ADMIN_EMAIL / BOOTSTRAP_ADMIN_PASSWORD are set but at least one user already exists. "
            + "These environment variables only take effect on a fresh database; remove them once the bootstrap account is in place.");
    }
}

app.Run();

static ClaimsIdentity BuildIdentity(User user)
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.Name, user.DisplayName),
        new(ClaimTypes.Email, user.Email),
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Role, user.Role.ToString()),
        new(HttpOrganizationContext.UserIdClaim, user.Id.ToString()),
        new(HttpOrganizationContext.OrganizationIdClaim, user.OrganizationId.ToString()),
        new("org_name", user.Organization?.Name ?? string.Empty),
    };
    if (user.IsSiteAdmin)
    {
        // The boolean claim feeds IOrganizationContext.IsSiteAdmin; the
        // role claim lets [Authorize(Roles = "SiteAdmin")] work without
        // a custom policy.
        claims.Add(new Claim(HttpOrganizationContext.SiteAdminClaim, "true"));
        claims.Add(new Claim(ClaimTypes.Role, HttpOrganizationContext.SiteAdminRole));
    }
    return new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
}

// Open-redirect guard: only allow same-site relative paths.
static string ResolveSafeReturn(string requestedReturn) =>
    !string.IsNullOrEmpty(requestedReturn)
        && Uri.IsWellFormedUriString(requestedReturn, UriKind.Relative)
        && requestedReturn.StartsWith('/')
        && !requestedReturn.StartsWith("//", StringComparison.Ordinal)
        && !requestedReturn.StartsWith("/\\", StringComparison.Ordinal)
            ? requestedReturn
            : "/";

static string ResolveIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? string.Empty;

static void WriteAttachmentHeaders(HttpContext ctx, string fileName)
{
    ctx.Response.ContentType = "application/zip";
    var cd = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
    cd.SetHttpFileName(fileName);
    ctx.Response.Headers.ContentDisposition = cd.ToString();
}

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

public partial class Program { }
