using ALDevToolbox.Components;
using ALDevToolbox.Data;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.OAuth;
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
    })
    // Bearer-token scheme for Personal Access Tokens. Sits alongside the
    // cookie scheme; only routes that opt in (currently /mcp) declare the
    // "McpBearer" authorisation policy. The handler mounts the same claim
    // set as the cookie path so IOrganizationContext resolves identically.
    .AddScheme<AuthenticationSchemeOptions, ALDevToolbox.Services.Account.PatAuthenticationHandler>(
        ALDevToolbox.Services.Account.PatAuthenticationHandler.AuthenticationScheme,
        _ => { });
builder.Services.AddAuthorization(options =>
{
    // McpBearer accepts EITHER a PAT (aldt_pat_…) OR an OAuth access token
    // issued by our own OpenIddict server. Same downstream claims, same
    // tenant scoping — the difference is invisible to the MCP tools.
    options.AddPolicy(McpBearerPolicy.Name, policy =>
    {
        policy.AuthenticationSchemes = new[]
        {
            ALDevToolbox.Services.Account.PatAuthenticationHandler.AuthenticationScheme,
            OpenIddict.Validation.AspNetCore.OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
        };
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(HttpOrganizationContext.UserIdClaim);
        policy.RequireClaim(HttpOrganizationContext.OrganizationIdClaim);
    });
    // Kept under the old name for one release so anything that hard-coded
    // "PAT" as the policy keeps working while it migrates.
    options.AddPolicy(ALDevToolbox.Services.Account.PatAuthenticationHandler.AuthenticationScheme, policy =>
    {
        policy.AuthenticationSchemes = new[]
        {
            ALDevToolbox.Services.Account.PatAuthenticationHandler.AuthenticationScheme,
            OpenIddict.Validation.AspNetCore.OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
        };
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(HttpOrganizationContext.UserIdClaim);
        policy.RequireClaim(HttpOrganizationContext.OrganizationIdClaim);
    });
});
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
        .UseNpgsql(connectionString, npgsql => npgsql
            .UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)
            // Object Explorer ingests write tens-of-thousands of rows per
            // Release; with the default Npgsql batch size (1000) EF builds
            // multi-megabyte parameter arrays per batch, and Base App's
            // oe_module_files rows (Content can be 50 KB each) push that
            // past the StringBuilder limit. Cap batch size so each
            // SaveChanges round trip stays comfortably bounded.
            .MaxBatchSize(100))
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));

builder.Services.AddScoped<TemplateService>();
builder.Services.AddScoped<SnippetService>();
builder.Services.AddScoped<SnippetSuggestionService>();
builder.Services.AddScoped<ModuleService>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<ApplicationVersionService>();
builder.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ReleaseImportService>();
builder.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ReleaseManagementService>();
builder.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ObjectExplorerService>();
builder.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ReferenceResolver>();
builder.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ReferenceSessionService>();
builder.Services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.ObjectExplorerLinks>();
builder.Services.AddSingleton<ALDevToolbox.Services.CacheBust>();
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
builder.Services.AddScoped<ALDevToolbox.Services.Account.RecoveryCodeService>();
builder.Services.AddScoped<ALDevToolbox.Services.Account.TotpService>();
builder.Services.AddScoped<ALDevToolbox.Services.Account.EmailMfaService>();
builder.Services.AddScoped<ALDevToolbox.Services.Account.PasskeyService>();
builder.Services.AddScoped<ALDevToolbox.Services.Account.PersonalAccessTokenService>();

// OAuth 2.1 server (OpenIddict) — adds the second accepted credential for
// /mcp so Claude.ai's directory and custom-connector flows can connect.
// Both schemes (PAT + OAuth) feed identical claims via OAuthClaimsTransformer,
// so MCP tools see the same principal whichever path authenticated the call.
// Discovery metadata is customised in OAuthEndpoints to advertise CIMD; the
// hand-rolled resource-metadata endpoint (RFC 9728) is registered there too.
builder.Services.AddOpenIddict()
    .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<AppDbContext>())
    .AddServer(o =>
    {
        o.SetAuthorizationEndpointUris("/oauth/authorize")
            .SetTokenEndpointUris("/oauth/token")
            .SetRevocationEndpointUris("/oauth/revoke")
            .SetIntrospectionEndpointUris("/oauth/introspect")
            .SetEndSessionEndpointUris("/oauth/logout");
        // DCR (RFC 7591) is hand-rolled at /oauth/register in OAuthEndpoints.cs
        // — OpenIddict 7.5.0's server builder doesn't expose a first-class
        // SetClientRegistrationEndpointUris(), so we write through
        // IOpenIddictApplicationManager from a minimal API instead, and
        // surface registration_endpoint via the discovery customisation.

        o.AllowAuthorizationCodeFlow()
            .AllowRefreshTokenFlow();
        // Claude requires S256 PKCE on every authorisation request; plain
        // is rejected by OpenIddict automatically once this is set.
        o.RequireProofKeyForCodeExchange();

        // Single resource scope today. offline_access enables refresh
        // tokens — Claude appends it when our discovery metadata
        // advertises it in scopes_supported.
        o.RegisterScopes(OpenIddict.Abstractions.OpenIddictConstants.Permissions.Scopes.Profile);
        o.RegisterScopes("mcp", OpenIddict.Abstractions.OpenIddictConstants.Scopes.OfflineAccess);

        // Reuse the existing Data Protection key ring (mounted on the
        // app-keys volume) for token format wrapping. Losing the key ring
        // already invalidates auth cookies and the system_settings SMTP
        // ciphertext, so OAuth tokens sharing its fate isn't a new failure
        // mode.
        o.UseDataProtection();

        // OpenIddict additionally requires signing + encryption keys for
        // the JWKS endpoint and its token-format fallback. UseDataProtection
        // alone doesn't supply these. Dev uses self-signed certs that
        // persist in the user X509 store (restart doesn't invalidate
        // issued tokens); prod uses ephemeral keys — access tokens are
        // 60 min and refresh tokens rotate, so a restart costs at worst
        // one extra trip through the consent screen per active user.
        // Cert-based prod keys are a follow-up; see .design/mcp-oauth.md.
        if (builder.Environment.IsDevelopment())
        {
            o.AddDevelopmentEncryptionCertificate()
                .AddDevelopmentSigningCertificate();
        }
        else
        {
            o.AddEphemeralEncryptionKey()
                .AddEphemeralSigningKey();
        }

        // Lifetimes — proactive refresh kicks in five minutes before
        // expiry, so 60-minute access tokens turn over comfortably.
        o.SetAccessTokenLifetime(TimeSpan.FromMinutes(60))
            .SetRefreshTokenLifetime(TimeSpan.FromDays(30));

        o.UseAspNetCore()
            .EnableAuthorizationEndpointPassthrough()
            .EnableTokenEndpointPassthrough();

        // Dev: HTTPS isn't terminated in front of us, so OpenIddict's
        // built-in transport-security check would refuse to start. Prod
        // runs behind a TLS-terminating proxy (UseForwardedHeaders is
        // installed below), so the check is satisfied by X-Forwarded-Proto.
        if (builder.Environment.IsDevelopment())
        {
            o.UseAspNetCore().DisableTransportSecurityRequirement();
        }

        // Discovery customisation. Two additions Claude needs:
        //   (1) Advertise the hand-rolled DCR endpoint (OpenIddict 7.5.0
        //       doesn't surface registration_endpoint itself).
        //   (2) Declare CIMD support — Claude only picks the CIMD path
        //       (URL-as-client_id) when client_id_metadata_document_supported
        //       is true AND token_endpoint_auth_methods_supported contains
        //       "none". Both already follow from running public-only PKCE
        //       clients, but we set them explicitly so the contract is
        //       readable from the metadata document.
        o.AddEventHandler<OpenIddict.Server.OpenIddictServerEvents.HandleConfigurationRequestContext>(b =>
            b.UseInlineHandler(context =>
            {
                context.Metadata["client_id_metadata_document_supported"] = true;
                var issuer = context.Issuer ?? context.BaseUri;
                if (issuer is not null)
                {
                    context.Metadata["registration_endpoint"] = new Uri(issuer, "/oauth/register").AbsoluteUri;
                }
                context.TokenEndpointAuthenticationMethods.Add(OpenIddict.Abstractions.OpenIddictConstants.ClientAuthenticationMethods.None);
                context.CodeChallengeMethods.Add(OpenIddict.Abstractions.OpenIddictConstants.CodeChallengeMethods.Sha256);
                return default;
            }));
    })
    .AddValidation(o =>
    {
        // Local issuer — no remote /introspect round-trip per request.
        o.UseLocalServer();
        o.UseAspNetCore();
        o.UseDataProtection();
    });
// Stamps the ALDevToolbox claim names (user_id, org_id, role, site_admin)
// on principals authenticated by OpenIddict's validation scheme. PAT and
// cookie principals already carry these claims; this only fires for
// OAuth access tokens.
builder.Services.AddScoped<IClaimsTransformation, ALDevToolbox.Services.OAuth.OAuthClaimsTransformer>();
builder.Services.AddScoped<ALDevToolbox.Services.OAuth.OAuthClientAdminService>();

// MCP server (Model Context Protocol). Mounted at /mcp by McpEndpoints; the
// PAT auth handler above turns Bearer tokens into the same claim set the
// cookie handler does, so the tool classes can rely on IOrganizationContext
// resolving exactly like a browser sign-in. Tool classes live under
// Services/Mcp/Tools/ and are picked up by WithToolsFromAssembly().
builder.Services.Configure<ALDevToolbox.Services.Mcp.McpOptions>(builder.Configuration.GetSection("Mcp"));
// In-memory MCP toggle cache. Singleton so NavMenu's per-render lookup
// doesn't hit the DB and race with status-code-pages scope teardown. Primed
// at startup and updated by SystemSettingsService.SaveAsync — see
// Services/Mcp/IMcpAvailability.cs.
builder.Services.AddSingleton<ALDevToolbox.Services.Mcp.McpAvailabilityState>();
builder.Services.AddSingleton<ALDevToolbox.Services.Mcp.IMcpAvailability>(
    sp => sp.GetRequiredService<ALDevToolbox.Services.Mcp.McpAvailabilityState>());
builder.Services.AddScoped<ALDevToolbox.Services.Mcp.Tools.WorkspaceTools>();
builder.Services.AddScoped<ALDevToolbox.Services.Mcp.Tools.SnippetTools>();
builder.Services.AddScoped<ALDevToolbox.Services.Mcp.Tools.ObjectExplorerTools>();
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();
// WebAuthn (passkeys). RP id / origins live in configuration; if RpId isn't
// set the passkey routes refuse with a clear error and the /account UI hides
// the section. See .design/auth-and-audit.md for the deployment requirement.
var webAuthnConfig = ALDevToolbox.Services.Account.WebAuthnConfig.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(webAuthnConfig);
builder.Services.AddFido2(options =>
{
    options.ServerDomain = string.IsNullOrEmpty(webAuthnConfig.RpId) ? "localhost" : webAuthnConfig.RpId;
    options.ServerName = webAuthnConfig.RpName;
    options.Origins = webAuthnConfig.Origins.Count > 0
        ? new HashSet<string>(webAuthnConfig.Origins)
        : new HashSet<string> { "https://localhost" };
    options.TimestampDriftTolerance = 300_000;
});
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<InviteService>();
builder.Services.AddScoped<SystemSettingsService>();
builder.Services.AddScoped<SiteAdminService>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddScoped<PerTenantBackupService>();
builder.Services.AddScoped<OffsiteBackupService>();
builder.Services.AddScoped<DatabaseUsageService>();
builder.Services.AddScoped<StorageQuotaGuard>();
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

// MCP runtime kill-switch: short-circuits /mcp requests to 404 when the
// SiteAdmin has the toggle off. Runs ahead of authentication/authorization
// and the antiforgery middleware so off-state isn't masked by an earlier
// 400/401. See Endpoints/McpEndpoints.cs.
app.UseMcpKillSwitch();

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
app.MapObjectExplorerEndpoints();
app.MapSiteAdminEndpoints();
app.MapMcpEndpoints();
app.MapOAuthEndpoints();

// Run migrations + bootstrap, then flip /readyz to green.
await StartupTasks.RunAsync(app);

app.Run();

public partial class Program { }
