using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Tools;

/// <summary>
/// "Is this tool switched on for the organisation acting right now?" — the one
/// question every surface that behaves differently for a disabled tool asks,
/// answered the same way the sidebar and the route gate answer it: a tool the
/// SiteAdmin switched off site-wide is off for everyone, and an organisation
/// can narrow that further by switching one off for itself.
///
/// <para>The sidebar (<c>NavMenu.razor</c>) and <see cref="ToolAccessGate"/>
/// read the <c>org_disabled_tools</c> claim inline because they run per render
/// and per request and must not touch the DB. This service exists for the
/// callers that are not a rendered page — chiefly the MCP tools, whose PAT and
/// OAuth principals carry no such claim (see
/// <c>PatAuthenticationHandler</c> and <c>OAuthClaimsTransformer</c>, which
/// mount the org and role claims but not this one). When the claim is there it
/// wins; otherwise the acting organisation's own
/// <see cref="Domain.Entities.Organization.DisabledTools"/> is read once and
/// held for the rest of the scope.</para>
/// </summary>
public sealed class ToolEnablement
{
    private readonly IToolAvailability _availability;
    private readonly IHttpContextAccessor _http;
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;

    // Resolved at most once per scope: a request that asks about two tools
    // should not read the organisation row twice.
    private HashSet<ToolKey>? _orgDisabled;

    public ToolEnablement(
        IToolAvailability availability,
        IHttpContextAccessor http,
        AppDbContext db,
        IOrganizationContext orgContext)
    {
        _availability = availability;
        _http = http;
        _db = db;
        _orgContext = orgContext;
    }

    /// <summary>
    /// True when <paramref name="key"/> is available to the organisation
    /// <paramref name="user"/> belongs to. The overload for a rendered page,
    /// which has the signed-in principal from the cascading auth state and so
    /// needs neither an <c>HttpContext</c> nor a query - the same two lines the
    /// sidebar runs.
    /// </summary>
    public bool IsEnabled(ToolKey key, ClaimsPrincipal? user) =>
        _availability.IsSiteEnabled(key) && !EndpointHelpers.ReadDisabledTools(user).Contains(key);

    /// <summary>
    /// True when <paramref name="key"/> is available to the acting
    /// organisation. Site state wins; the per-org opt-out only narrows it.
    /// </summary>
    public async Task<bool> IsEnabledAsync(ToolKey key, CancellationToken ct = default)
    {
        if (!_availability.IsSiteEnabled(key)) return false;
        return !(await OrgDisabledAsync(ct)).Contains(key);
    }

    private async Task<HashSet<ToolKey>> OrgDisabledAsync(CancellationToken ct)
    {
        if (_orgDisabled is not null) return _orgDisabled;

        // A cookie principal carries the claim, so the browser path costs
        // nothing. The claim may legitimately be empty (nothing switched off),
        // hence the presence check rather than a check on the parsed set.
        var user = _http.HttpContext?.User;
        if (user?.FindFirst(EndpointHelpers.DisabledToolsClaim) is not null)
        {
            return _orgDisabled = EndpointHelpers.ReadDisabledTools(user);
        }

        if (_orgContext.CurrentOrganizationId is not { } orgId)
        {
            return _orgDisabled = new HashSet<ToolKey>();
        }

        // Organization carries no query filter to escape (see the note in
        // AppDbContext.OnModelCreating), so the row is pinned by id here - and
        // that id comes from the authenticated principal, never from input.
        var row = await _db.Organizations
            .AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => new { o.DisabledTools, o.McpEnabled })
            .FirstOrDefaultAsync(ct);
        if (row is null) return _orgDisabled = new HashSet<ToolKey>();

        var disabled = ToolCatalog.ParseDisabled(row.DisabledTools);
        // MCP keeps its own flag on the organisation; folding it in here means
        // callers ask one question for every tool, as the claim lets them.
        if (!row.McpEnabled) disabled.Add(ToolKey.Mcp);
        return _orgDisabled = disabled;
    }
}
