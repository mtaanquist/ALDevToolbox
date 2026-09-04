using System.Security.Cryptography;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// The two routes of the GitHub App install handshake, modelled on
/// <see cref="EntraAuthEndpoints"/>. An org Admin posts to
/// <c>/admin/github/connect</c>, picks the organisation on GitHub, and GitHub
/// sends them back to <c>/github/setup</c> with the installation id.
///
/// <para><strong>This is not a sign-in provider.</strong> Neither route issues a
/// cookie or creates a user; both run inside the Admin's existing session, which
/// is also why neither needs to cross the tenant fence — the acting organisation
/// comes from the caller's own cookie. See <c>.design/github-integration.md</c>.</para>
/// </summary>
internal static class GitHubAppEndpoints
{
    /// <summary>Data Protection purpose for the handshake's <c>state</c> parameter.</summary>
    public const string StateProtectionPurpose = "ALDevToolbox.GitHub.InstallState";

    /// <summary>Where GitHub sends the admin back to after the install.</summary>
    public const string SetupCallbackPath = "/github/setup";

    /// <summary>The tab both routes redirect back to, in success and failure.</summary>
    private const string RepositoriesTab = "/admin/administration/repositories";

    /// <summary>
    /// How long a started handshake stays valid. Long enough to read GitHub's
    /// permission screen, short enough that an abandoned state is not a
    /// standing invitation.
    /// </summary>
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(15);

    public static IEndpointRouteBuilder MapGitHubAppEndpoints(this IEndpointRouteBuilder app)
    {
        // "Connect a GitHub organisation" on Administration -> Repositories.
        app.MapPost("/admin/github/connect", async (
            HttpContext ctx,
            SystemSettingsService settings,
            IDataProtectionProvider protection,
            IMemoryCache cache,
            IOrganizationContext org,
            IAntiforgery antiforgery,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            var view = await settings.GetGitHubAppViewAsync(ct);
            if (!view.IsConfigured)
            {
                RedirectWithMessage(ctx,
                    "GitHub is not set up on this server yet. Ask whoever runs AL Dev Toolbox to add it, then try again.");
                return;
            }

            var orgId = org.CurrentOrganizationId;
            var userId = org.CurrentUserId;
            if (orgId is null || userId is null)
            {
                ctx.Response.Redirect(RouteConstants.Login);
                return;
            }

            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            // Single-use: the callback consumes the cache entry, so a replayed
            // (or shared) callback URL finds nothing and is refused.
            cache.Set(NonceCacheKey(nonce), true, StateLifetime);
            var state = protection.CreateProtector(StateProtectionPurpose).Protect(
                $"{orgId.Value}|{userId.Value}|{nonce}|{clock.GetUtcNow().ToUnixTimeSeconds()}");

            ctx.Response.Redirect(
                $"https://github.com/apps/{Uri.EscapeDataString(view.AppSlug!)}/installations/new"
                + $"?state={Uri.EscapeDataString(state)}");
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.AdminRole));

        // GitHub's post-install redirect. Runs inside the same admin's session,
        // so the acting organisation still comes from the cookie.
        app.MapGet(SetupCallbackPath, async (
            HttpContext ctx,
            GitHubAppClient github,
            GitHubConnectionService connection,
            IDataProtectionProvider protection,
            IMemoryCache cache,
            IOrganizationContext org,
            TimeProvider clock,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("GitHubAppSetup");

            if (!TryConsumeState(ctx.Request.Query["state"].ToString(), protection, cache, org, clock))
            {
                logger.LogWarning("Rejected a GitHub install callback whose state did not validate.");
                // An org that is already connected has almost certainly just
                // revisited a used link, and telling it to press a Connect
                // button that isn't on the page would send it looking for one.
                var alreadyConnected = (await connection.GetStatusAsync(ct)).IsConnected;
                RedirectWithMessage(ctx, alreadyConnected
                    ? "That setup link had already been used. Nothing changed - your GitHub organisation is still connected."
                    : "That link has expired or was already used. Start again from Connect a GitHub organisation.");
                return;
            }

            if (!long.TryParse(ctx.Request.Query["installation_id"].ToString(), out var installationId)
                || installationId <= 0)
            {
                // setup_action=request means an owner still has to approve the
                // install; GitHub sends no installation id in that case.
                RedirectWithMessage(ctx,
                    "GitHub has not finished installing the app yet. An owner of your GitHub organisation needs to approve it - come back and connect once they have.");
                return;
            }

            try
            {
                var installation = await github.GetInstallationAsync(installationId, ct);
                await connection.ConnectAsync(installation, ct);
                ctx.Response.Redirect($"{RepositoriesTab}?{RouteConstants.OkQuery}=github-connected");
            }
            catch (PlanValidationException ex)
            {
                RedirectWithMessage(ctx, ex.Errors.First().Value);
            }
            catch (GitHubAppNotConfiguredException)
            {
                RedirectWithMessage(ctx,
                    "GitHub is not set up on this server yet. Ask whoever runs AL Dev Toolbox to add it, then try again.");
            }
            catch (GitHubApiException ex)
            {
                logger.LogWarning(ex,
                    "GitHub refused to describe installation {InstallationId} during setup.", installationId);
                RedirectWithMessage(ctx, $"GitHub could not confirm the installation: {ex.Message}");
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.AdminRole));

        return app;
    }

    private static string NonceCacheKey(string nonce) => $"github-install-state:{nonce}";

    private static void RedirectWithMessage(HttpContext ctx, string message) =>
        ctx.Response.Redirect($"{RepositoriesTab}?{RouteConstants.MsgQuery}=" + Uri.EscapeDataString(message));

    /// <summary>
    /// Validates the round-tripped <c>state</c> and burns it. Four things have
    /// to hold: it decrypts (so we minted it), its nonce is still in the cache
    /// (so it has not been used), it is inside <see cref="StateLifetime"/>, and
    /// it names the organisation and user whose cookie is on this request — a
    /// callback URL pasted into someone else's session must not connect anything.
    /// </summary>
    private static bool TryConsumeState(
        string? state,
        IDataProtectionProvider protection,
        IMemoryCache cache,
        IOrganizationContext org,
        TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(state)) return false;

        string payload;
        try
        {
            payload = protection.CreateProtector(StateProtectionPurpose).Unprotect(state);
        }
        catch (CryptographicException)
        {
            return false;
        }

        var parts = payload.Split('|');
        if (parts.Length != 4) return false;
        if (!int.TryParse(parts[0], out var orgId)) return false;
        if (!int.TryParse(parts[1], out var userId)) return false;
        if (!long.TryParse(parts[3], out var issuedAt)) return false;

        var age = clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(issuedAt);
        if (age < TimeSpan.Zero || age > StateLifetime) return false;
        if (org.CurrentOrganizationId != orgId || org.CurrentUserId != userId) return false;

        var key = NonceCacheKey(parts[2]);
        if (!cache.TryGetValue(key, out bool _)) return false;
        cache.Remove(key);
        return true;
    }
}
