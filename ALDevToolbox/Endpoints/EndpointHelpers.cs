using System.Security.Claims;
using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Statics shared across the endpoint extension classes: antiforgery
/// validation, claim-principal construction, attachment headers, etc.
/// </summary>
internal static class EndpointHelpers
{
    public static ClaimsIdentity BuildIdentity(User user)
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
            // Cached per-org MCP opt-out. Authoritative check still runs at
            // /mcp request time against the DB so a stale claim can't smuggle
            // a banned org back in; this claim only feeds the nav-link
            // visibility so the link disappears without a DB hit per render.
            new("org_mcp_enabled", (user.Organization?.McpEnabled ?? true) ? "true" : "false"),
        };
        if (user.IsSiteAdmin)
        {
            // The boolean claim feeds IOrganizationContext.IsSiteAdmin; the
            // role claim lets [Authorize(Roles = "SiteAdmin")] work without
            // a custom policy.
            claims.Add(new Claim(HttpOrganizationContext.SiteAdminClaim, "true"));
            claims.Add(new Claim(ClaimTypes.Role, HttpOrganizationContext.SiteAdminRole));
        }
        // Tags the cookie when the signed-in user belongs to the singleton
        // system org so IOrganizationContext.IsSystemOrganization resolves
        // without a per-request DB lookup. Sign-in paths Include the org nav
        // already (see AccountService.TryLoginAsync and friends).
        if (user.Organization?.IsSystem == true)
        {
            claims.Add(new Claim(HttpOrganizationContext.SystemOrgClaim, "true"));
        }
        return new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Marks the auth cookie persistent so it survives browser restarts for
    /// the full <c>ExpireTimeSpan</c> window (see Program.cs). Without this the
    /// cookie is a session cookie and the browser drops it on close, defeating
    /// the long expiry. Returns a fresh instance per call — the auth stack
    /// mutates the properties bag during sign-in.
    /// </summary>
    public static Microsoft.AspNetCore.Authentication.AuthenticationProperties PersistentSignIn() =>
        new() { IsPersistent = true };

    /// <summary>Open-redirect guard: only allow same-site relative paths.</summary>
    public static string ResolveSafeReturn(string requestedReturn) =>
        !string.IsNullOrEmpty(requestedReturn)
            && Uri.IsWellFormedUriString(requestedReturn, UriKind.Relative)
            && requestedReturn.StartsWith('/')
            && !requestedReturn.StartsWith("//", StringComparison.Ordinal)
            && !requestedReturn.StartsWith("/\\", StringComparison.Ordinal)
                ? requestedReturn
                : "/";

    public static string ResolveIp(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress?.ToString() ?? string.Empty;

    /// <summary>
    /// Per-user cache key for endpoint-scoped in-memory state (e.g. the
    /// References-session cache). Falls back to the auth name when the
    /// NameIdentifier claim is absent; returns null for anonymous calls so
    /// callers can short-circuit with 401.
    /// </summary>
    public static string? OwnerKey(HttpContext ctx)
    {
        var user = ctx.User;
        if (user?.Identity?.IsAuthenticated != true) return null;
        return user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.Identity.Name
            ?? null;
    }

    public static void WriteAttachmentHeaders(HttpContext ctx, string fileName)
    {
        ctx.Response.ContentType = "application/zip";
        var cd = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
        cd.SetHttpFileName(fileName);
        ctx.Response.Headers.ContentDisposition = cd.ToString();
    }

    public static async Task<bool> ValidateAntiforgeryAsync(HttpContext ctx, IAntiforgery antiforgery, CancellationToken ct)
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

    public static void SetGenerationCompleteCookie(HttpContext ctx, string token)
    {
        if (string.IsNullOrEmpty(token)) return;
        ctx.Response.Cookies.Append("aldt-gen", token, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            Path = "/",
            MaxAge = TimeSpan.FromSeconds(30),
        });
    }

    public const string MfaPendingCookieName = "alwb_mfa";
    public const string MfaProtectionPurpose = "ALDevToolbox.MfaPending";
    public const string OneShotInviteCookieName = "alwb_invite_link";
    public const string OneShotInviteProtectionPurpose = "ALDevToolbox.OneShotInviteUrl";
    public const string OneShotRecoveryCodesCookieName = "alwb_recovery_codes";
    public const string OneShotRecoveryCodesProtectionPurpose = "ALDevToolbox.OneShotRecoveryCodes";
    public const string OneShotPatCookieName = "alwb_pat_created";
    public const string OneShotPatProtectionPurpose = "ALDevToolbox.OneShotPat";
    public static readonly TimeSpan MfaCookieLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan OneShotCookieLifetime = TimeSpan.FromSeconds(60);

    public sealed record MfaPending(int UserId, bool TotpEnabled, bool EmailMfaEnabled, DateTime IssuedAt, string ReturnUrl);

    public static void SetMfaPendingCookie(HttpContext ctx, IDataProtectionProvider protection, MfaPending state)
    {
        var protector = protection.CreateProtector(MfaProtectionPurpose);
        var payload = protector.Protect(JsonSerializer.Serialize(state));
        ctx.Response.Cookies.Append(MfaPendingCookieName, payload, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            Path = "/",
            MaxAge = MfaCookieLifetime,
        });
    }

    public static MfaPending? ReadMfaPendingCookie(HttpContext ctx, IDataProtectionProvider protection, TimeProvider clock)
    {
        if (!ctx.Request.Cookies.TryGetValue(MfaPendingCookieName, out var raw) || string.IsNullOrEmpty(raw)) return null;
        try
        {
            var protector = protection.CreateProtector(MfaProtectionPurpose);
            var json = protector.Unprotect(raw);
            var state = JsonSerializer.Deserialize<MfaPending>(json);
            if (state is null) return null;
            if (clock.GetUtcNow().UtcDateTime - state.IssuedAt > MfaCookieLifetime) return null;
            return state;
        }
        catch
        {
            return null;
        }
    }

    public static void ClearMfaPendingCookie(HttpContext ctx) =>
        ctx.Response.Cookies.Delete(MfaPendingCookieName);

    public const string SignupVerifiedCookieName = "alwb_signup_verified";
    public const string SignupVerifiedProtectionPurpose = "ALDevToolbox.SignupVerified";
    public static readonly TimeSpan SignupVerifiedCookieLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Carries the freshly-verified signup email from the verify step to the
    /// details step. A UX hint only — the completion endpoint re-validates the
    /// email against a verified, uncompleted <c>pending_signups</c> row, so a
    /// forged or replayed cookie is inert. Matches the signed-cookie posture of
    /// <see cref="MfaPending"/>.
    /// </summary>
    public sealed record SignupVerified(int PendingSignupId, string Email, DateTime IssuedAt);

    public static void SetSignupVerifiedCookie(HttpContext ctx, IDataProtectionProvider protection, SignupVerified state)
    {
        var protector = protection.CreateProtector(SignupVerifiedProtectionPurpose);
        var payload = protector.Protect(JsonSerializer.Serialize(state));
        ctx.Response.Cookies.Append(SignupVerifiedCookieName, payload, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            Path = "/",
            MaxAge = SignupVerifiedCookieLifetime,
        });
    }

    public static SignupVerified? ReadSignupVerifiedCookie(HttpContext ctx, IDataProtectionProvider protection, TimeProvider clock)
    {
        if (!ctx.Request.Cookies.TryGetValue(SignupVerifiedCookieName, out var raw) || string.IsNullOrEmpty(raw)) return null;
        try
        {
            var protector = protection.CreateProtector(SignupVerifiedProtectionPurpose);
            var state = JsonSerializer.Deserialize<SignupVerified>(protector.Unprotect(raw));
            if (state is null) return null;
            if (clock.GetUtcNow().UtcDateTime - state.IssuedAt > SignupVerifiedCookieLifetime) return null;
            return state;
        }
        catch
        {
            return null;
        }
    }

    public static void ClearSignupVerifiedCookie(HttpContext ctx) =>
        ctx.Response.Cookies.Delete(SignupVerifiedCookieName);

    /// <summary>
    /// Writes a protected one-shot cookie: a short-lived, Data-Protection-signed
    /// value the reader consumes once and clears. Each named one-shot helper
    /// delegates here with its own cookie name and protection purpose so the
    /// shared <see cref="CookieOptions"/> and lifetime stay in one place.
    /// </summary>
    private static void SetOneShotCookie(HttpContext ctx, IDataProtectionProvider protection, string name, string purpose, string value)
    {
        var protector = protection.CreateProtector(purpose);
        var payload = protector.Protect(value);
        ctx.Response.Cookies.Append(name, payload, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            Path = "/",
            MaxAge = OneShotCookieLifetime,
        });
    }

    /// <summary>
    /// Reads and clears a protected one-shot cookie, returning the unprotected
    /// payload or null when the cookie is absent, empty, or fails to unprotect
    /// (tamper or expiry — treated as absent). The clear is skipped when the
    /// response has already started: once the prerender HTML has been flushed
    /// (e.g. when this runs from a Blazor OnAfterRenderAsync on the interactive
    /// circuit), touching Response.Cookies throws "Headers are read-only". The
    /// cookie has a short TTL and is consumed on this read either way, so
    /// silently skipping the delete is safe.
    /// </summary>
    private static string? ReadAndClearOneShotCookie(HttpContext ctx, IDataProtectionProvider protection, string name, string purpose)
    {
        if (!ctx.Request.Cookies.TryGetValue(name, out var raw) || string.IsNullOrEmpty(raw)) return null;
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.Cookies.Delete(name);
        }
        try
        {
            var protector = protection.CreateProtector(purpose);
            return protector.Unprotect(raw);
        }
        catch
        {
            return null;
        }
    }

    public static void SetOneShotInviteCookie(HttpContext ctx, IDataProtectionProvider protection, string url) =>
        SetOneShotCookie(ctx, protection, OneShotInviteCookieName, OneShotInviteProtectionPurpose, url);

    public static string? ReadAndClearOneShotInviteCookie(HttpContext ctx, IDataProtectionProvider protection) =>
        ReadAndClearOneShotCookie(ctx, protection, OneShotInviteCookieName, OneShotInviteProtectionPurpose);

    public static void SetOneShotRecoveryCodesCookie(HttpContext ctx, IDataProtectionProvider protection, IEnumerable<string> codes) =>
        SetOneShotCookie(ctx, protection, OneShotRecoveryCodesCookieName, OneShotRecoveryCodesProtectionPurpose, string.Join('\n', codes));

    public static string[]? ReadAndClearOneShotRecoveryCodesCookie(HttpContext ctx, IDataProtectionProvider protection) =>
        ReadAndClearOneShotCookie(ctx, protection, OneShotRecoveryCodesCookieName, OneShotRecoveryCodesProtectionPurpose)
            ?.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Stash for the one-shot Personal Access Token reveal screen. The
    /// plaintext appears only here — once the cookie is consumed, the user
    /// can't see it again. Lifetime matches the other one-shot cookies.
    /// </summary>
    public sealed record OneShotPat(int TokenId, string Plaintext, string Name, DateTime CreatedAt, DateTime? ExpiresAt);

    public static void SetOneShotPatCookie(HttpContext ctx, IDataProtectionProvider protection, OneShotPat value) =>
        SetOneShotCookie(ctx, protection, OneShotPatCookieName, OneShotPatProtectionPurpose, JsonSerializer.Serialize(value));

    public static OneShotPat? ReadAndClearOneShotPatCookie(HttpContext ctx, IDataProtectionProvider protection)
    {
        var raw = ReadAndClearOneShotCookie(ctx, protection, OneShotPatCookieName, OneShotPatProtectionPurpose);
        if (raw is null) return null;
        try
        {
            return JsonSerializer.Deserialize<OneShotPat>(raw);
        }
        catch
        {
            return null;
        }
    }
}
