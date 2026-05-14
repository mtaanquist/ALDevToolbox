using System.Security.Claims;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;

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
}
