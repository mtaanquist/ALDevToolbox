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

builder.Services.AddScoped<FolderTreeHydrator>();
builder.Services.AddScoped<TemplateService>();
builder.Services.AddScoped<RecipeService>();
builder.Services.AddScoped<RecipeSuggestionService>();
builder.Services.AddScoped<ModuleService>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<ApplicationVersionService>();
builder.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.TranslationImportService>();
builder.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ReleaseImportService>();
builder.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.DvdDownloadService>();
builder.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ReleaseManagementService>();
builder.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ObjectExplorerService>();
builder.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.TranslationQueryService>();
builder.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ReleaseComparisonService>();
builder.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ObjectSearchService>();
builder.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ReferenceQueryService>();
builder.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.SourceViewerService>();
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
builder.Services.AddScoped<OrganizationAdminService>();
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
//
// Persistent signing + encryption keys live on the same app-keys volume as
// the Data Protection ring (separate env var so operators who want to
// isolate them can). Loaded before AddOpenIddict because OpenIddict's
// builder needs them at config time, not at first use. Falls back to
// in-memory keys with a warning when the directory isn't writable —
// previously the prod default was always-ephemeral, so the fallback path
// is a strict superset of what shipped before.
var oauthKeyDir = Environment.GetEnvironmentVariable("OAUTH_KEY_DIR")
    ?? Environment.GetEnvironmentVariable("DATA_PROTECTION_KEY_DIR")
    ?? "/var/lib/aldevtoolbox/dp-keys";
var oauthKeyLogger = LoggerFactory.Create(b => b.AddSimpleConsole(o => { o.SingleLine = true; o.UseUtcTimestamp = true; }))
    .CreateLogger(typeof(ALDevToolbox.Services.OAuth.OAuthKeyMaterial).FullName!);
var (oauthSigningKey, oauthEncryptionKey) = ALDevToolbox.Services.OAuth.OAuthKeyMaterial.LoadOrCreate(oauthKeyDir, oauthKeyLogger);

// Stable per-deployment id (same volume as the keys) used to fingerprint
// off-site dumps so a restore won't clobber the DB with a neighbour
// deployment's dump from a shared bucket. Registered as a singleton.
var deploymentIdentity = ALDevToolbox.Services.DeploymentIdentity.LoadOrCreate(oauthKeyDir, oauthKeyLogger);
builder.Services.AddSingleton(deploymentIdentity);

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
        o.RegisterScopes("mcp", OpenIddict.Abstractions.OpenIddictConstants.Scopes.OfflineAccess);

        // MCP clients (Claude web's custom connector, Claude Code) include
        // a `resource` parameter on every authorise + token request per the
        // MCP 2025-11-25 spec (RFC 8707 — Resource Indicators). OpenIddict
        // gates such requests in two places, both of which we opt out of:
        //
        //   * DisableResourceValidation removes the stock ValidateResources
        //     handler that compares the request value against the in-memory
        //     OpenIddictServerOptions.Resources allowlist (populated at
        //     startup via RegisterResources()).
        //   * IgnoreResourcePermissions removes ValidateResourcePermissions,
        //     the per-client check that requires the client's Permissions
        //     collection to carry "rsrc:" + <resource_url>. CIMD- and
        //     DCR-registered clients are created without that permission
        //     and we don't know the canonical URL when CimdClientResolver
        //     runs in a way that survives existing rows.
        //
        // We don't know the public host when the host builds (no PublicUrl
        // config; deployments use the request's Forwarded-* headers as the
        // source of truth), and attempts to mutate state dynamically from
        // pre-validator event handlers didn't take effect — see the
        // PR #191 / #192 retrospectives. Both checks are defence-in-depth
        // for servers fronting multiple resources; ALDevToolbox exposes a
        // single protected resource (/mcp), so disabling them only removes a
        // cross-resource confused-deputy guard that doesn't apply here.
        // NB: McpBearerPolicy gates /mcp on authentication + the user/org
        // claims — it does NOT separately assert the token audience, so don't
        // rely on it as an audience check. If a second protected resource is
        // ever added, re-enable resource validation (or add an explicit
        // audience requirement) before doing so.
        //
        // TODO: Revisit once OpenIddict ships native DCR / CIMD support
        // (tracked in openiddict/openiddict-core#2404, targeted at 7.6.0)
        // — that release will likely introduce a more idiomatic way to
        // register resources dynamically from a CIMD application descriptor,
        // at which point both opt-outs can come back on.
        o.DisableResourceValidation();
        o.IgnoreResourcePermissions();

        // Reuse the existing Data Protection key ring (mounted on the
        // app-keys volume) for token format wrapping. Losing the key ring
        // already invalidates auth cookies and the system_settings SMTP
        // ciphertext, so OAuth tokens sharing its fate isn't a new failure
        // mode.
        o.UseDataProtection();

        // OpenIddict additionally requires signing + encryption keys for
        // the JWKS endpoint and its token-format fallback. UseDataProtection
        // alone doesn't supply these. Loaded once at startup from the
        // app-keys volume via OAuthKeyMaterial — same trust boundary as
        // the Data Protection ring (anyone who can read app-keys can
        // already steal cookies + the SMTP password). Persisting them
        // means a container restart no longer invalidates every issued
        // access + refresh token, so Claude doesn't have to re-consent
        // on every redeploy.
        o.AddSigningKey(oauthSigningKey)
            .AddEncryptionKey(oauthEncryptionKey);

        // Lifetimes — proactive refresh kicks in five minutes before
        // expiry, so 60-minute access tokens turn over comfortably.
        o.SetAccessTokenLifetime(TimeSpan.FromMinutes(60))
            .SetRefreshTokenLifetime(TimeSpan.FromDays(30));

        // Only /oauth/authorize is passed through — the consent UI lives in
        // a Razor page and we craft the principal ourselves in
        // OAuthEndpoints.MapAuthorizeComplete. /oauth/token has no
        // customisation: OpenIddict already has the principal stored against
        // the auth code and refresh token, so it can issue the response on
        // its own. Enabling token-endpoint passthrough without registering a
        // matching route handler silently drops the response after validation
        // and Claude surfaces "Authorization with the MCP server failed".
        o.UseAspNetCore()
            .EnableAuthorizationEndpointPassthrough();

        // Dev: HTTPS isn't terminated in front of us, so OpenIddict's
        // built-in transport-security check would refuse to start. Prod
        // runs behind a TLS-terminating proxy (UseForwardedHeaders is
        // installed below), so the check is satisfied by X-Forwarded-Proto.
        if (builder.Environment.IsDevelopment())
        {
            o.UseAspNetCore().DisableTransportSecurityRequirement();
        }

        // Discovery customisation. Three additions MCP clients need:
        //   (1) Advertise the hand-rolled DCR endpoint (OpenIddict 7.5.0
        //       doesn't surface registration_endpoint itself).
        //   (2) Declare CIMD support — MCP clients pick the CIMD path
        //       (URL-as-client_id) when client_id_metadata_document_supported
        //       is true AND token_endpoint_auth_methods_supported lists the
        //       method they want to use.
        //   (3) Advertise both "none" (Claude's public PKCE clients) and
        //       "private_key_jwt" (ChatGPT's signed-assertion clients), plus
        //       the RS256 signing algorithm ChatGPT's CIMD documents declare.
        //       Missing either silently demotes that vendor to DCR-only or
        //       refuses outright.
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
                context.TokenEndpointAuthenticationMethods.Add(OpenIddict.Abstractions.OpenIddictConstants.ClientAuthenticationMethods.PrivateKeyJwt);
                context.Metadata["token_endpoint_auth_signing_alg_values_supported"] = new OpenIddict.Abstractions.OpenIddictParameter(
                    System.Text.Json.JsonSerializer.SerializeToElement(new[] { "RS256" }));
                context.CodeChallengeMethods.Add(OpenIddict.Abstractions.OpenIddictConstants.CodeChallengeMethods.Sha256);
                return default;
            }));

        // CIMD resolver — Claude's hosted surfaces identify themselves with
        // an HTTPS URL as their client_id (e.g.
        // https://claude.ai/oauth/mcp-oauth-client-metadata). Without this
        // handler OpenIddict's standard ValidateClientId rejects the request
        // with ID2052 because no oauth_applications row matches. Runs ahead
        // of every built-in validator so the row exists by the time
        // OpenIddict's own lookup fires.
        o.AddEventHandler<OpenIddict.Server.OpenIddictServerEvents.ValidateAuthorizationRequestContext>(b =>
            b.UseScopedHandler<ALDevToolbox.Services.OAuth.CimdClientResolver>()
             .SetOrder(int.MinValue + 100_000));
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
// The CIMD resolver fetches a client metadata document over HTTPS. Named
// HttpClient gives us per-call timeout/UA control without leaking the
// configuration to every other caller.
// SSRF guard: the resolver fetches attacker-supplied URLs, so dial only
// publicly routable IPs (defeats DNS rebinding because the check runs on the
// address we actually connect to) and refuse redirects (an HTTPS URL must not
// be able to 302 us onto an internal http:// target).
builder.Services.AddHttpClient(nameof(ALDevToolbox.Services.OAuth.CimdClientResolver))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = ALDevToolbox.Services.OAuth.SsrfGuard.ConnectAsync,
    });
builder.Services.AddScoped<ALDevToolbox.Services.OAuth.CimdClientResolver>();

// DVD download client for the Object Explorer "import release from URL" flow.
// Same SSRF guard as the CIMD client (dial only publicly routable IPs), but
// redirects are allowed: Microsoft download URLs commonly 302 to a CDN, and the
// ConnectCallback re-checks every hop's IP so a redirect still can't reach an
// internal target. The long timeout covers the multi-GB body.
builder.Services.AddHttpClient(nameof(ALDevToolbox.Services.ObjectExplorer.DvdDownloadService), client =>
    {
        client.Timeout = TimeSpan.FromMinutes(20);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        ConnectCallback = ALDevToolbox.Services.OAuth.SsrfGuard.ConnectAsync,
    });

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
builder.Services.AddScoped<ALDevToolbox.Services.Mcp.Tools.CookbookTools>();
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
// Off-site restore download jobs: a singleton job tracker shared between
// the SiteAdmin endpoint that enqueues and the worker that drains, and a
// sequential BackgroundService that processes one download at a time.
// Sequential because the bottleneck is the local disk on the
// `app-backups` volume, not S3 throughput.
builder.Services.AddSingleton<OffsiteRestoreJobs>();
builder.Services.AddHostedService<OffsiteRestoreWorker>();
builder.Services.AddScoped<DatabaseUsageService>();
builder.Services.AddScoped<StorageQuotaGuard>();
// MaintenanceModeState is a process-local flag — singleton lifetime so the
// middleware and BackupService share the same instance.
builder.Services.AddSingleton<MaintenanceModeState>();
// IconCatalog reads the vendored Lucide SVGs from embedded resources once
// at startup; singleton so we pay the parse cost a single time per process.
builder.Services.AddSingleton<IconCatalog>();
// MarkdownRenderer builds the Markdig pipeline once on construction and
// reuses it for every render; safe as a singleton, no per-request state.
builder.Services.AddSingleton<MarkdownRenderer>();
// The scheduler runs in the background; opt-out via DISABLE_BACKUP_SCHEDULER=1
// for environments (tests, CI) that don't want a background timer to start
// chasing pg_dump.
if (Environment.GetEnvironmentVariable("DISABLE_BACKUP_SCHEDULER") != "1")
{
    builder.Services.AddHostedService<BackupScheduler>();
}
// Daily VACUUM over the Object Explorer content tables. Same opt-out
// pattern as the backup scheduler so tests can disable the timer.
if (Environment.GetEnvironmentVariable("DISABLE_OE_VACUUM_SCHEDULER") != "1")
{
    builder.Services.AddHostedService<ALDevToolbox.Services.ObjectExplorer.ObjectExplorerVacuumScheduler>();
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

// Per-org strong-auth gate. When an org has RequireStrongAuth=true, any
// authenticated request from a member of that org who hasn't yet enrolled
// TOTP, email-MFA, or a passkey is redirected to /account?required=1 (or
// gets a 403 for non-GET). Runs after authentication so it can read the
// cookie's user_id claim. See Endpoints/StrongAuthGate.cs.
app.UseStrongAuthGate();

// Maintenance mode (M18): 503 every non-SiteAdmin request while a restore
// is mid-flight. See Endpoints/MaintenanceModeMiddleware.cs.
app.UseMaintenanceMode();

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
app.MapCookbookEndpoints();
app.MapLegacyRedirects();
app.MapSiteAdminEndpoints();
app.MapMcpEndpoints();
app.MapOAuthEndpoints();

// Run migrations + bootstrap, then flip /readyz to green.
await StartupTasks.RunAsync(app);

app.Run();

public partial class Program { }
