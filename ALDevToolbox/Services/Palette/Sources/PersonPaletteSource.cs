using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.SingleTenant;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Palette.Sources;

/// <summary>
/// The people in the caller's organisation, by name, for the admins who manage
/// them. See <c>.design/command-palette.md</c>, "Sources".
///
/// <para><b>Where a row lands.</b> There is no page per user: an org Admin
/// manages people on the Users tab of Administration, one table of everyone, so
/// a row lands on that table at the person's own row
/// (<c>/admin/administration/users?user=12</c>, which the tab marks and focuses). A SiteAdmin who is not an Admin
/// of their organisation cannot open that tab, so for them the row lands on the
/// site-wide user list filtered to the name instead - the page the sidebar
/// gives them for people. Which of the two applies is decided once, by the
/// gate, and a search the gate never approved answers nothing.</para>
///
/// <para><b>What a row shows.</b> The name, and the role - plus "disabled" for
/// someone who can no longer sign in, since that is usually why an admin is
/// looking them up. Never the email address: the palette's rule is that a
/// person's contact details are neither searched nor shown, and the name is
/// what someone types.</para>
///
/// <para><b>The fence.</b> Users are org-scoped by the EF query filter, and this
/// reads through it - no <c>IgnoreQueryFilters()</c>, so a SiteAdmin's palette
/// finds the people in their own organisation, not the whole site's. The set is
/// the Users tab's own: everyone but pending signups, which that tab shows under
/// their own heading as requests rather than people.</para>
/// </summary>
public sealed class PersonPaletteSource : IPaletteSource
{
    /// <summary>
    /// An organisation's users are a small table of human-typed names, so like
    /// Solutions this projects them and lets the ranking fold accents, rather
    /// than pre-filtering in SQL. Bounded all the same.
    /// </summary>
    public const int MaxUsersScanned = 2000;

    /// <summary>The Users tab of Administration - the org Admin's page for people.</summary>
    public const string OrganizationUsersHref = "/admin/administration/users";

    /// <summary>The site-wide user list, which takes a <c>?q=</c> filter.</summary>
    public const string SiteUsersHref = "/site-admin/users";

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ISingleTenantMode _singleTenant;

    private Landing _landing = Landing.None;

    public PersonPaletteSource(AppDbContext db, IOrganizationContext orgContext, ISingleTenantMode singleTenant)
    {
        _db = db;
        _orgContext = orgContext;
        _singleTenant = singleTenant;
    }

    public string Id => "people";

    public string Label => "People";

    public int Order => PaletteGroupOrder.People;

    /// <summary>
    /// Admins only, each landing on the page their role opens. An org Admin
    /// gets the Users tab wherever the sidebar offers it (everywhere but the
    /// system organisation of a multi-tenant site, which administers people from
    /// the site console); a SiteAdmin gets that console. A plain member or an
    /// Editor gets nothing - the Users tab refuses them too.
    /// </summary>
    public Task<bool> IsAvailableAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        _landing = Landing.None;
        if (user.Identity?.IsAuthenticated != true) return Task.FromResult(false);

        var showsPerOrgContent = !_orgContext.IsSystemOrganization || _singleTenant.IsEnabled;
        if (user.IsInRole(nameof(UserRole.Admin)) && showsPerOrgContent)
        {
            _landing = Landing.OrganizationUsers;
        }
        else if (user.IsInRole(HttpOrganizationContext.SiteAdminRole))
        {
            _landing = Landing.SiteUsers;
        }

        return Task.FromResult(_landing != Landing.None);
    }

    public async Task<IReadOnlyList<PaletteCandidate>> SearchAsync(
        PaletteQuery query, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        // Fail closed: a search the gate did not approve - or was never asked
        // about - has nowhere to land, so it finds no one.
        if (!query.IsUsable || _landing == Landing.None) return [];

        var people = await _db.Users
            .AsNoTracking()
            .Where(u => u.Status != UserStatus.Pending)
            .OrderBy(u => u.DisplayName)
            .Take(MaxUsersScanned)
            .Select(u => new { u.Id, u.DisplayName, u.Role, u.Status })
            .ToListAsync(ct);

        var candidates = people
            .Where(p => !string.IsNullOrWhiteSpace(p.DisplayName))
            .Select(p => new PaletteCandidate(
                "person", p.DisplayName, Subtitle(p.Role, p.Status), Href(p.Id, p.DisplayName)));

        return PaletteRanking.Rank(query, candidates).Take(limit).Select(m => m.Candidate).ToList();
    }

    /// <summary>The role, as the Users tab's role column names it.</summary>
    private static string Subtitle(UserRole role, UserStatus status) =>
        status == UserStatus.Disabled ? $"{role} - disabled" : role.ToString();

    private string Href(int id, string displayName) => _landing == Landing.OrganizationUsers
        ? $"{OrganizationUsersHref}?user={id}"
        : $"{SiteUsersHref}?q={Uri.EscapeDataString(displayName)}";

    private enum Landing
    {
        None,
        OrganizationUsers,
        SiteUsers,
    }
}
