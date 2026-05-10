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
}
