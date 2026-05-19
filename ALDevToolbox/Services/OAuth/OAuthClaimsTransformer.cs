using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

namespace ALDevToolbox.Services.OAuth;

/// <summary>
/// Bridges OpenIddict's access-token claims (<c>sub</c>, <c>org</c>, scope)
/// onto the claim names the rest of the app reads
/// (<see cref="HttpOrganizationContext.UserIdClaim"/>,
/// <see cref="HttpOrganizationContext.OrganizationIdClaim"/>,
/// <see cref="HttpOrganizationContext.SiteAdminClaim"/>, role). Matching the
/// shape <see cref="ALDevToolbox.Services.Account.PatAuthenticationHandler"/>
/// already mounts means MCP tools see exactly the same principal regardless
/// of which credential authenticated the request.
///
/// Only runs when the principal was authenticated by OpenIddict's validation
/// scheme — every other auth scheme (cookie, PAT) already mounts the full
/// claim set itself.
/// </summary>
public sealed class OAuthClaimsTransformer : IClaimsTransformation
{
    private readonly AppDbContext _db;

    public OAuthClaimsTransformer(AppDbContext db)
    {
        _db = db;
    }

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = principal.Identity as ClaimsIdentity;
        if (identity is null || !identity.IsAuthenticated)
        {
            return principal;
        }
        if (identity.AuthenticationType != OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
        {
            return principal;
        }
        // Idempotent: once we've stamped the bridge claims, re-running is a no-op.
        if (identity.HasClaim(c => c.Type == HttpOrganizationContext.UserIdClaim))
        {
            return principal;
        }

        var subject = principal.FindFirstValue(OpenIddictConstants.Claims.Subject);
        if (string.IsNullOrEmpty(subject) || !int.TryParse(subject, out var userId))
        {
            return principal;
        }

        // The 'org' claim is stamped on the access token by the consent flow
        // (Services/OAuth/OAuthAuthorizeService.SignInAsync). It's a hard
        // requirement — tokens minted without it can't be mapped to a
        // tenant, and the McpBearer policy will reject them.
        var orgClaim = principal.FindFirstValue("org");
        if (string.IsNullOrEmpty(orgClaim) || !int.TryParse(orgClaim, out var orgId))
        {
            return principal;
        }

        var user = await _db.Users
            .IgnoreQueryFilters()
            .Include(u => u.Organization)
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null || user.Status != UserStatus.Active)
        {
            return principal;
        }
        // Defensive: the user must still belong to the org the token claims.
        // Org membership today is scalar (User.OrganizationId), so this is a
        // direct equality check. When multi-org support lands the check
        // becomes a contains() on the user's org list — same place.
        if (user.OrganizationId != orgId)
        {
            return principal;
        }

        identity.AddClaim(new Claim(HttpOrganizationContext.UserIdClaim, userId.ToString()));
        identity.AddClaim(new Claim(HttpOrganizationContext.OrganizationIdClaim, orgId.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.DisplayName));
        identity.AddClaim(new Claim(ClaimTypes.Email, user.Email));
        identity.AddClaim(new Claim(ClaimTypes.Role, user.Role.ToString()));
        if (user.IsSiteAdmin)
        {
            identity.AddClaim(new Claim(HttpOrganizationContext.SiteAdminClaim, "true"));
            identity.AddClaim(new Claim(ClaimTypes.Role, HttpOrganizationContext.SiteAdminRole));
        }
        if (user.Organization?.IsSystem == true)
        {
            identity.AddClaim(new Claim(HttpOrganizationContext.SystemOrgClaim, "true"));
        }
        return principal;
    }
}
