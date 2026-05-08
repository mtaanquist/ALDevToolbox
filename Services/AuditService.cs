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

    public AuditService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Returns the most recent audit entries across every audited entity, newest
    /// first. Drives the <c>/admin/audit</c> overview page.
    /// </summary>
    public Task<List<AuditLogEntry>> GetRecentAsync(int limit = 200, CancellationToken ct = default)
    {
        return _db.AuditLog
            .AsNoTracking()
            .OrderByDescending(e => e.Timestamp)
            .ThenByDescending(e => e.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns the full history for a specific entity, newest first. Drives the
    /// <c>&lt;AuditHistoryPanel&gt;</c> component embedded on admin edit pages.
    /// </summary>
    public Task<List<AuditLogEntry>> GetForEntityAsync(
        AuditEntityType entityType,
        int entityId,
        CancellationToken ct = default)
    {
        return _db.AuditLog
            .AsNoTracking()
            .Where(e => e.EntityType == entityType && e.EntityId == entityId)
            .OrderByDescending(e => e.Timestamp)
            .ThenByDescending(e => e.Id)
            .ToListAsync(ct);
    }
}
