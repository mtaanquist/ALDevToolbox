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
    /// Filtered + paginated read for the per-org Admin audit page.
    /// All filters are optional; any combination narrows the set.
    /// </summary>
    public async Task<(List<AuditLogEntry> Items, int Total)> GetPagedAsync(
        AuditFilter filter,
        int skip,
        int take,
        CancellationToken ct = default)
    {
        var orgId = _orgContext.CurrentOrganizationId;
        var q = _db.AuditLog
            .AsNoTracking()
            .Where(e => e.OrganizationId == orgId);

        if (filter.EntityType is { } entityType)
        {
            q = q.Where(e => e.EntityType == entityType);
        }
        if (filter.Action is { } action)
        {
            q = q.Where(e => e.Action == action);
        }
        if (!string.IsNullOrWhiteSpace(filter.EntityId)
            && int.TryParse(filter.EntityId, out var entityIdValue))
        {
            q = q.Where(e => e.EntityId == entityIdValue);
        }
        if (!string.IsNullOrWhiteSpace(filter.Actor))
        {
            var pattern = "%" + filter.Actor.Trim() + "%";
            q = q.Where(e => EF.Functions.ILike(e.ChangedBy, pattern));
        }
        if (filter.From is { } from)
        {
            q = q.Where(e => e.Timestamp >= from);
        }
        if (filter.To is { } to)
        {
            q = q.Where(e => e.Timestamp <= to);
        }

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(e => e.Timestamp)
            .ThenByDescending(e => e.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
        return (items, total);
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

/// <summary>
/// Optional filters applied by <see cref="AuditService.GetPagedAsync"/>.
/// <c>EntityId</c> is a string so the UI can pass the raw query-string
/// value through; the service parses it.
/// </summary>
public record AuditFilter(
    AuditEntityType? EntityType,
    AuditAction? Action,
    string? EntityId,
    string? Actor,
    DateTime? From,
    DateTime? To);
