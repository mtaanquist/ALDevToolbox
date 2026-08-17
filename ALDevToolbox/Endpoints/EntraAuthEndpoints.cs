using System.Security.Claims;
using ALDevToolbox.Services.Account;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Microsoft (Entra ID) sign-in endpoints — issue #552 slice 2. The
/// challenge endpoint routes the sign-in to an organisation and hands the
/// OIDC handler its per-request app-registration credentials via
/// <see cref="AuthenticationProperties"/>; the callback logic lives in
/// <see cref="OnTicketReceivedAsync"/> (wired from the handler options in
/// <c>Program.cs</c>) and defers every security decision to
/// <see cref="EntraSignInService"/>.
/// </summary>
internal static class EntraAuthEndpoints
{
    public const string AuthenticationScheme = "EntraId";

    /// <summary>
    /// The redirect URI path registered in Entra. Displayed to admins on
    /// both settings pages — changing it invalidates every registration,
    /// so don't. See .design/auth-and-audit.md.
    /// </summary>
    public const string CallbackPath = "/signin-microsoft";

    // AuthenticationProperties item keys carried through the handshake.
    public const string OrgIdItem = "entra_org_id";
    public const string ClientIdItem = "entra_client_id";
    public const string ConfigSourceItem = "entra_config_source";
    public const string LoginHintItem = "entra_login_hint";

    public static IEndpointRouteBuilder MapEntraAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Posted from the login form ("Sign in with Microsoft" submits the
        // same form via formaction, so the typed email rides along as the
        // routing + login hint).
        app.MapPost("/auth/entra/challenge", async (
            HttpContext ctx, EntraSignInService entra, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var email = form["Email"].ToString();
            var safeReturn = ResolveSafeReturn(form["ReturnUrl"].ToString());

            var (config, errorCode) = await entra.ResolveChallengeAsync(email, ct);
            if (config is null)
            {
                ctx.Response.Redirect($"{RouteConstants.Login}?{RouteConstants.ErrQuery}={errorCode}&return={Uri.EscapeDataString(safeReturn)}");
                return;
            }

            var properties = new AuthenticationProperties { RedirectUri = safeReturn };
            properties.Items[OrgIdItem] = config.OrganizationId.ToString();
            properties.Items[ClientIdItem] = config.ClientId;
            properties.Items[ConfigSourceItem] = config.ConfigSource;
            if (!string.IsNullOrWhiteSpace(email)) properties.Items[LoginHintItem] = email.Trim();
            await ctx.ChallengeAsync(AuthenticationScheme, properties);
        });

        return app;
    }

    /// <summary>
    /// The OIDC handler's terminal event. Always handles the response
    /// itself — the handler never signs its (external) principal into our
    /// cookie; a successful resolution mints the standard
    /// <see cref="EndpointHelpers.BuildIdentity"/> cookie instead, so the
    /// downstream (query filters, revalidation, role gates) can't tell a
    /// federated sign-in from a password one.
    /// </summary>
    public static async Task OnTicketReceivedAsync(TicketReceivedContext ctx)
    {
        ctx.HandleResponse();
        var ct = ctx.HttpContext.RequestAborted;
        var principal = ctx.Principal!;
        var token = new EntraTokenIdentity(
            TenantId: principal.FindFirst("tid")?.Value ?? string.Empty,
            ObjectId: principal.FindFirst("oid")?.Value ?? string.Empty,
            Email: principal.FindFirst("preferred_username")?.Value ?? principal.FindFirst("email")?.Value,
            DisplayName: principal.FindFirst("name")?.Value);
        var safeReturn = ResolveSafeReturn(ctx.Properties?.RedirectUri ?? "/");

        if (token.TenantId.Length == 0 || token.ObjectId.Length == 0)
        {
            ctx.Response.Redirect($"{RouteConstants.Login}?{RouteConstants.ErrQuery}=entra-failed");
            return;
        }

        var entra = ctx.HttpContext.RequestServices.GetRequiredService<EntraSignInService>();
        var result = await entra.CompleteAsync(token, ResolveIp(ctx.HttpContext), ct);

        if (result.Outcome == EntraCompletionOutcome.Success && result.User is not null)
        {
            // Reload with the Organization nav so BuildIdentity can stamp
            // the org-name / MCP / tool claims, mirroring TryLoginAsync.
            var identity = BuildIdentity(result.User);
            await ctx.HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity), PersistentSignIn());
            ctx.Response.Redirect(safeReturn);
            return;
        }

        var code = result.Outcome switch
        {
            EntraCompletionOutcome.PendingApproval => "entra-pending",
            EntraCompletionOutcome.AccountPending => "pending",
            EntraCompletionOutcome.AccountDisabled => "disabled",
            EntraCompletionOutcome.TenantNotAllowed => "entra-tenant",
            EntraCompletionOutcome.Ambiguous => "entra-ambiguous",
            EntraCompletionOutcome.EmailMissing => "entra-failed",
            EntraCompletionOutcome.EmailTakenElsewhere => "entra-email-taken",
            _ => "entra-failed",
        };
        ctx.Response.Redirect($"{RouteConstants.Login}?{RouteConstants.ErrQuery}={code}");
    }
}
