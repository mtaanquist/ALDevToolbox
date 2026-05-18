using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// Bearer-token authentication handler for Personal Access Tokens. Reads
/// <c>Authorization: Bearer aldt_pat_...</c>, validates the token via
/// <see cref="PersonalAccessTokenService"/>, and mounts a
/// <see cref="ClaimsPrincipal"/> with the same claim names the cookie
/// handler does — so <see cref="IOrganizationContext"/> and every EF
/// query filter resolve identically to a browser sign-in.
///
/// Scheme name: <see cref="AuthenticationScheme"/>. Routes that should
/// accept PATs (currently just <c>/mcp</c>) declare
/// <see cref="Microsoft.AspNetCore.Authorization.AuthorizationPolicy"/>
/// against this scheme; cookie routes keep their existing default policy.
/// </summary>
public sealed class PatAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string AuthenticationScheme = "PAT";

    private readonly PersonalAccessTokenService _tokens;

    public PatAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        PersonalAccessTokenService tokens)
        : base(options, logger, encoder)
    {
        _tokens = tokens;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header))
        {
            return AuthenticateResult.NoResult();
        }
        var raw = header.ToString();
        if (string.IsNullOrEmpty(raw) || !raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }
        var token = raw["Bearer ".Length..].Trim();
        if (!token.StartsWith(PersonalAccessTokenService.TokenPrefix, StringComparison.Ordinal))
        {
            // Some other Bearer flavour — leave it for another handler.
            return AuthenticateResult.NoResult();
        }

        var principal = await _tokens.ValidateAsync(token, Context.RequestAborted);
        if (principal is null)
        {
            return AuthenticateResult.Fail("Invalid or expired personal access token.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, principal.DisplayName),
            new(ClaimTypes.Email, principal.Email),
            new(ClaimTypes.NameIdentifier, principal.UserId.ToString()),
            new(ClaimTypes.Role, principal.Role.ToString()),
            new(HttpOrganizationContext.UserIdClaim, principal.UserId.ToString()),
            new(HttpOrganizationContext.OrganizationIdClaim, principal.OrganizationId.ToString()),
            new("pat_id", principal.TokenId.ToString()),
        };
        if (principal.IsSiteAdmin)
        {
            claims.Add(new Claim(HttpOrganizationContext.SiteAdminClaim, "true"));
            claims.Add(new Claim(ClaimTypes.Role, HttpOrganizationContext.SiteAdminRole));
        }
        if (principal.IsSystemOrganization)
        {
            claims.Add(new Claim(HttpOrganizationContext.SystemOrgClaim, "true"));
        }
        var identity = new ClaimsIdentity(claims, AuthenticationScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), AuthenticationScheme);
        return AuthenticateResult.Success(ticket);
    }
}
