using System.Security.Claims;
using ALDevToolbox.Domain.Entities;

namespace ALDevToolbox.Services;

/// <summary>
/// HTTP-backed <see cref="IOrganizationContext"/>. Reads the <c>org_id</c> and
/// <c>user_id</c> claims placed on the auth cookie at sign-in time.
/// </summary>
public sealed class HttpOrganizationContext : IOrganizationContext
{
    public const string OrganizationIdClaim = "org_id";
    public const string UserIdClaim = "user_id";
    public const string SiteAdminClaim = "site_admin";
    public const string SystemOrgClaim = "system_org";
    public const string SiteAdminRole = "SiteAdmin";
    /// <summary>
    /// Org-scoped role names as they land in the <see cref="ClaimTypes.Role"/>
    /// claim — emitted from <c>UserRole.ToString()</c>, so they're bound to the
    /// enum via <c>nameof</c> rather than re-spelled as literals at every
    /// <c>RequireRole</c> / <c>[Authorize]</c> call site.
    /// </summary>
    public const string AdminRole = nameof(UserRole.Admin);
    public const string EditorRole = nameof(UserRole.Editor);
    /// <summary>Path prefix for SiteAdmin pages; auth events match against this to return 404 instead of 403.</summary>
    public const string SiteAdminPathPrefix = "/site-admin";

    private readonly IHttpContextAccessor _http;

    public HttpOrganizationContext(IHttpContextAccessor http)
    {
        _http = http;
    }

    // Claims win when present (a real request). The ambient fallback only
    // applies off-request — i.e. inside the release-import background worker,
    // which re-installs the submitting user's own org identity. See
    // AmbientOrganizationScope.
    public int? CurrentOrganizationId =>
        GetIntClaim(OrganizationIdClaim) ?? AmbientOrganizationScope.Current?.OrganizationId;
    public int? CurrentUserId =>
        GetIntClaim(UserIdClaim) ?? AmbientOrganizationScope.Current?.UserId;
    public bool IsSiteAdmin =>
        _http.HttpContext is { } ctx
            ? ctx.User?.FindFirst(SiteAdminClaim)?.Value == "true"
            : AmbientOrganizationScope.Current?.IsSiteAdmin ?? false;
    public bool IsSystemOrganization =>
        _http.HttpContext is { } ctx
            ? ctx.User?.FindFirst(SystemOrgClaim)?.Value == "true"
            : AmbientOrganizationScope.Current?.IsSystemOrganization ?? false;
    public int OrganizationIdForFilter => CurrentOrganizationId ?? 0;

    private int? GetIntClaim(string type)
    {
        var value = _http.HttpContext?.User?.FindFirstValue(type);
        return int.TryParse(value, out var id) ? id : null;
    }
}

/// <summary>
/// Mutable <see cref="IOrganizationContext"/> for non-HTTP code paths
/// (seed bootstrap, background migrations, tests). Set the property to scope
/// subsequent <c>AppDbContext</c> reads to a particular organisation.
/// </summary>
public sealed class AmbientOrganizationContext : IOrganizationContext
{
    public int? CurrentOrganizationId { get; set; }
    public int? CurrentUserId { get; set; }
    public bool IsSiteAdmin { get; set; }
    public bool IsSystemOrganization { get; set; }
    public int OrganizationIdForFilter => CurrentOrganizationId ?? 0;
}
