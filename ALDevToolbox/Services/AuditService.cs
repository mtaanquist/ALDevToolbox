using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Read-side queries over the audit log. Mutation of the log itself happens via
/// <see cref="AuditInterceptor"/>; nothing in the application writes to it directly.
/// </summary>
public class AuditService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;

    public AuditService(AppDbContext db, IOrganizationContext orgContext)
    {
        _db = db;
        _orgContext = orgContext;
    }

    /// <summary>
    /// Returns the most recent audit entries across every audited entity for
    /// the acting user's organisation, newest first. Drives the
    /// <c>/admin/audit</c> overview page.
    /// </summary>
    public Task<List<AuditLogEntry>> GetRecentAsync(int limit = 200, CancellationToken ct = default)
    {
        var orgId = _orgContext.CurrentOrganizationId;
        return _db.AuditLog
            .AsNoTracking()
            .Where(e => e.OrganizationId == orgId)
            .OrderByDescending(e => e.Timestamp)
            .ThenByDescending(e => e.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns the most recent <paramref name="limit"/> audit entries for a
    /// specific entity, newest first. Drives the
    /// <c>&lt;AuditHistoryPanel&gt;</c> component embedded on admin edit pages.
    /// </summary>
    public Task<List<AuditLogEntry>> GetForEntityAsync(
        AuditEntityType entityType,
        int entityId,
        int limit = 200,
        CancellationToken ct = default)
    {
        var orgId = _orgContext.CurrentOrganizationId;
        return _db.AuditLog
            .AsNoTracking()
            .Where(e => e.EntityType == entityType
                        && e.EntityId == entityId
                        && e.OrganizationId == orgId)
            .OrderByDescending(e => e.Timestamp)
            .ThenByDescending(e => e.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns a single audit entry by id, scoped to the acting user's
    /// organisation, or <c>null</c> if no such row exists in scope. Used by
    /// the diff viewer to load the row a user clicked through to.
    /// </summary>
    public Task<AuditLogEntry?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var orgId = _orgContext.CurrentOrganizationId;
        return _db.AuditLog
            .AsNoTracking()
            .Where(e => e.Id == id && e.OrganizationId == orgId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Resolves the "after" snapshot for an audit entry by looking up the
    /// next-newer audit row for the same entity. The audit log captures the
    /// state *before* each change (see <c>.design/auth-and-audit.md</c>), so
    /// the state *after* an entry is the "before" of the next entry. Returns
    /// <c>null</c> when no later row exists (the change is the most recent
    /// for that entity, or the entity was deleted).
    /// </summary>
    public Task<AuditLogEntry?> GetNextForEntityAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        var orgId = _orgContext.CurrentOrganizationId;
        return _db.AuditLog
            .AsNoTracking()
            .Where(e => e.EntityType == entry.EntityType
                        && e.EntityId == entry.EntityId
                        && e.OrganizationId == orgId
                        && (e.Timestamp > entry.Timestamp
                            || (e.Timestamp == entry.Timestamp && e.Id > entry.Id)))
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.Id)
            .FirstOrDefaultAsync(ct);
    }
}
