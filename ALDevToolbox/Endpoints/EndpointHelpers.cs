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

    public static void SetOneShotInviteCookie(HttpContext ctx, IDataProtectionProvider protection, string url)
    {
        var protector = protection.CreateProtector(OneShotInviteProtectionPurpose);
        var payload = protector.Protect(url);
        ctx.Response.Cookies.Append(OneShotInviteCookieName, payload, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            Path = "/",
            MaxAge = OneShotCookieLifetime,
        });
    }

    public static string? ReadAndClearOneShotInviteCookie(HttpContext ctx, IDataProtectionProvider protection)
    {
        if (!ctx.Request.Cookies.TryGetValue(OneShotInviteCookieName, out var raw) || string.IsNullOrEmpty(raw)) return null;
        ctx.Response.Cookies.Delete(OneShotInviteCookieName);
        try
        {
            var protector = protection.CreateProtector(OneShotInviteProtectionPurpose);
            return protector.Unprotect(raw);
        }
        catch
        {
            return null;
        }
    }

    public static void SetOneShotRecoveryCodesCookie(HttpContext ctx, IDataProtectionProvider protection, IEnumerable<string> codes)
    {
        var protector = protection.CreateProtector(OneShotRecoveryCodesProtectionPurpose);
        var payload = protector.Protect(string.Join('\n', codes));
        ctx.Response.Cookies.Append(OneShotRecoveryCodesCookieName, payload, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            Path = "/",
            MaxAge = OneShotCookieLifetime,
        });
    }

    public static string[]? ReadAndClearOneShotRecoveryCodesCookie(HttpContext ctx, IDataProtectionProvider protection)
    {
        if (!ctx.Request.Cookies.TryGetValue(OneShotRecoveryCodesCookieName, out var raw) || string.IsNullOrEmpty(raw)) return null;
        ctx.Response.Cookies.Delete(OneShotRecoveryCodesCookieName);
        try
        {
            var protector = protection.CreateProtector(OneShotRecoveryCodesProtectionPurpose);
            return protector.Unprotect(raw).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch
        {
            return null;
        }
    }
}
