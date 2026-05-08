using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Read-side helpers for runtime templates. CRUD operations for the admin UI
/// are added in a later milestone; for now this exposes only the queries the
/// /templates page and the New Workspace dropdown need.
/// </summary>
public class TemplateService
{
    private readonly AppDbContext _db;

    public TemplateService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Returns every active runtime template (i.e. not soft-deleted), ordered by
    /// runtime version. <paramref name="includeDeprecated"/> is <c>true</c> for
    /// admin views and <c>false</c> for end-user dropdowns.
    /// </summary>
    public Task<List<RuntimeTemplate>> GetTemplatesAsync(bool includeDeprecated = true)
    {
        var query = _db.RuntimeTemplates
            .AsNoTracking()
            .Where(t => t.DeletedAt == null);

        if (!includeDeprecated)
            query = query.Where(t => !t.Deprecated);

        return query
            .OrderBy(t => t.Runtime)
            .ThenBy(t => t.Name)
            .Include(t => t.Folders.OrderBy(f => f.Ordering))
            .ToListAsync();
    }

    /// <summary>
    /// Returns every active module, ordered by display name.
    /// </summary>
    public Task<List<Module>> GetModulesAsync(bool includeDeprecated = true)
    {
        var query = _db.Modules
            .AsNoTracking()
            .Where(m => m.DeletedAt == null);

        if (!includeDeprecated)
            query = query.Where(m => !m.Deprecated);

        return query
            .OrderBy(m => m.Name)
            .Include(m => m.Dependencies.OrderBy(d => d.Ordering))
            .ToListAsync();
    }
}
