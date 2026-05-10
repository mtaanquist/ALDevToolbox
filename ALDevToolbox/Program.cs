using System.Security.Claims;
using ALDevToolbox.Components;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
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
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IOrganizationContext, HttpOrganizationContext>();

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
builder.Services.AddScoped<ApplicationVersionService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<SeedService>();
builder.Services.AddScoped<WorkspaceConfigService>();
builder.Services.AddScoped<GenerationService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();

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

// /auth/signup — creates a Pending user (and optionally a Pending org).
// On success we email the org's active admins; failures log a warning but
// don't roll back so a misconfigured SMTP doesn't strand new signups.
app.MapPost("/auth/signup", async (
    HttpContext ctx,
    AccountService accounts,
    AppDbContext db,
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

        if (org is not null && email.IsConfigured)
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

        ctx.Response.Redirect("/signup?ok=" + (outcome == SignupOutcome.OrganizationProvisioned ? "new-org" : "pending"));
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
    if (!email.IsConfigured)
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
    if (email.IsConfigured)
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

    if (email.IsConfigured && !string.IsNullOrEmpty(requesterEmail))
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
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(stopping);
        bootstrapLogger.LogInformation("Bootstrap admin {Email} created in Default organisation.", bootstrapEmail);
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
