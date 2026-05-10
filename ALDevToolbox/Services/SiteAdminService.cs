using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Cross-organisation projection of a user. Returned by
/// <see cref="SiteAdminService.SearchUsersAsync"/> so the SiteAdmin
/// /site-admin/users page can render one row per (user, org) pairing
/// without joining in the page.
/// </summary>
public sealed record SiteAdminUserRow(
    int UserId,
    string Email,
    string DisplayName,
    UserRole Role,
    UserStatus Status,
    bool IsSiteAdmin,
    int OrganizationId,
    string OrganizationName,
    string OrganizationSlug,
    DateTime? LastLoginAt);

/// <summary>
/// One row per audit_log entry, with the organisation's name eagerly joined
/// so the SiteAdmin audit page can render cross-org rows in a single pass.
/// </summary>
public sealed record SiteAdminAuditRow(
    int Id,
    DateTime Timestamp,
    string ChangedBy,
    int? OrganizationId,
    string? OrganizationName,
    AuditEntityType EntityType,
    int EntityId,
    AuditAction Action,
    string? SnapshotJson);

/// <summary>
/// SiteAdmin (hosting-operator) operations that span organisations. Every
/// method explicitly bypasses EF query filters via <c>IgnoreQueryFilters()</c>
/// — by design — and guards on a <see cref="RequireSiteAdmin"/> check so
/// org-scoped admins can't escalate through these calls. See
/// <c>.design/milestones.md</c>, M17.
/// </summary>
public sealed class SiteAdminService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<SiteAdminService> _logger;

    public SiteAdminService(AppDbContext db, IOrganizationContext orgContext, ILogger<SiteAdminService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _logger = logger;
    }

    /// <summary>
    /// Searches users across every organisation. The query is a
    /// case-insensitive substring match against email and display name; an
    /// empty query returns the most recently signed-in users so the page
    /// has something to show on first load.
    /// </summary>
    public async Task<List<SiteAdminUserRow>> SearchUsersAsync(string? query, int limit = 100, CancellationToken ct = default)
    {
        var trimmed = (query ?? string.Empty).Trim().ToLowerInvariant();
        var users = _db.Users.IgnoreQueryFilters()
            .Include(u => u.Organization)
            .AsNoTracking();

        if (!string.IsNullOrEmpty(trimmed))
        {
            users = users.Where(u =>
                u.Email.ToLower().Contains(trimmed)
                || u.DisplayName.ToLower().Contains(trimmed));
        }

        var rows = await users
            .OrderByDescending(u => u.LastLoginAt ?? u.CreatedAt)
            .ThenBy(u => u.Email)
            .Take(limit)
            .Select(u => new SiteAdminUserRow(
                u.Id,
                u.Email,
                u.DisplayName,
                u.Role,
                u.Status,
                u.IsSiteAdmin,
                u.OrganizationId,
                u.Organization!.Name,
                u.Organization.Slug,
                u.LastLoginAt))
            .ToListAsync(ct);

        return rows;
    }

    /// <summary>
    /// Returns every organisation the given email is a member of. Drives the
    /// per-user detail rendering on the SiteAdmin users page when an admin
    /// clicks through from the search results.
    /// </summary>
    public async Task<List<SiteAdminUserRow>> GetMembershipsAsync(string email, CancellationToken ct = default)
    {
        var normalised = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (normalised.Length == 0) return new List<SiteAdminUserRow>();

        return await _db.Users.IgnoreQueryFilters()
            .Include(u => u.Organization)
            .AsNoTracking()
            .Where(u => u.Email == normalised)
            .OrderBy(u => u.Organization!.Name)
            .Select(u => new SiteAdminUserRow(
                u.Id,
                u.Email,
                u.DisplayName,
                u.Role,
                u.Status,
                u.IsSiteAdmin,
                u.OrganizationId,
                u.Organization!.Name,
                u.Organization.Slug,
                u.LastLoginAt))
            .ToListAsync(ct);
    }

    /// <summary>Promotes a user to SiteAdmin. The change is audited via the standard interceptor.</summary>
    public async Task PromoteAsync(int userId, CancellationToken ct = default)
    {
        RequireSiteAdmin();
        var user = await LoadUserAsync(userId, ct);
        if (!user.IsSiteAdmin)
        {
            user.IsSiteAdmin = true;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Promoted {Email} (user {UserId}) to SiteAdmin.", user.Email, user.Id);
        }
    }

    /// <summary>
    /// Demotes a user from SiteAdmin. Refuses to demote the last remaining
    /// SiteAdmin so the deployment never ends up with no hosting operator.
    /// </summary>
    public async Task DemoteAsync(int userId, CancellationToken ct = default)
    {
        RequireSiteAdmin();
        var user = await LoadUserAsync(userId, ct);
        if (!user.IsSiteAdmin) return;

        var siteAdminCount = await _db.Users.IgnoreQueryFilters()
            .CountAsync(u => u.IsSiteAdmin && u.Status != UserStatus.Disabled, ct);
        if (siteAdminCount <= 1)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["LastSiteAdmin"] = "You can't demote the last remaining SiteAdmin."
            });
        }

        user.IsSiteAdmin = false;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Demoted {Email} (user {UserId}) from SiteAdmin.", user.Email, user.Id);
    }

    /// <summary>
    /// Cross-organisation audit search. All filter arguments are optional;
    /// an unfiltered call returns the newest <paramref name="limit"/> rows
    /// across every organisation.
    /// </summary>
    public async Task<List<SiteAdminAuditRow>> SearchAuditAsync(
        AuditEntityType? entityType,
        int? organizationId,
        string? actorContains,
        DateTime? fromUtc,
        DateTime? toUtc,
        int limit = 200,
        CancellationToken ct = default)
    {
        RequireSiteAdmin();
        var q = _db.AuditLog.IgnoreQueryFilters().AsNoTracking().AsQueryable();

        if (entityType is { } et) q = q.Where(e => e.EntityType == et);
        if (organizationId is int oid) q = q.Where(e => e.OrganizationId == oid);
        if (!string.IsNullOrWhiteSpace(actorContains))
        {
            var needle = actorContains.Trim().ToLowerInvariant();
            q = q.Where(e => e.ChangedBy.ToLower().Contains(needle));
        }
        if (fromUtc is { } from) q = q.Where(e => e.Timestamp >= from);
        if (toUtc is { } to) q = q.Where(e => e.Timestamp <= to);

        return await q
            .OrderByDescending(e => e.Timestamp)
            .ThenByDescending(e => e.Id)
            .Take(limit)
            .Select(e => new SiteAdminAuditRow(
                e.Id,
                e.Timestamp,
                e.ChangedBy,
                e.OrganizationId,
                e.Organization == null ? null : e.Organization.Name,
                e.EntityType,
                e.EntityId,
                e.Action,
                e.SnapshotJson))
            .ToListAsync(ct);
    }

    /// <summary>List of all organisations for the audit search filter dropdown.</summary>
    public Task<List<Organization>> ListOrganizationsAsync(CancellationToken ct = default)
    {
        RequireSiteAdmin();
        return _db.Organizations.IgnoreQueryFilters()
            .AsNoTracking()
            .OrderBy(o => o.Name)
            .ToListAsync(ct);
    }

    private async Task<User> LoadUserAsync(int userId, CancellationToken ct)
    {
        var user = await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["UserId"] = "User not found." });
        }
        return user;
    }

    /// <summary>
    /// Throws when the acting principal is not a SiteAdmin. Guards every
    /// mutation in this service so an organisation admin who happens to
    /// reach a SiteAdmin endpoint by URL guessing can't do anything.
    /// </summary>
    private void RequireSiteAdmin()
    {
        if (!_orgContext.IsSiteAdmin)
        {
            throw new InvalidOperationException(
                "SiteAdmin context is required for this operation. The endpoint should already be 404-guarded.");
        }
    }
}
