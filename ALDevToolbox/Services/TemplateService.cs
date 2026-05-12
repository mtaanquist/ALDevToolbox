using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Read- and write-side service for runtime templates. The query helpers drive
/// the user-facing dropdowns and the templates browser; the admin CRUD methods
/// back the <c>/admin/templates*</c> pages.
/// </summary>
/// <remarks>
/// This service is in transitional state after Issue #54 unified the Core /
/// modules folder model under <c>[[extensions]]</c>. The query side has been
/// updated to load the new <see cref="WorkspaceExtension"/> graph; the
/// write-side (CreateAsync, UpdateAsync, the folder/file reconcilers) is
/// pending its rewrite around the new tables and throws
/// <see cref="NotImplementedException"/>. The admin Edit form compiles against
/// the unchanged <see cref="TemplateInput"/> record shape; the form's submit
/// path is currently inert.
/// </remarks>
public class TemplateService
{
    /// <summary>
    /// Accepts BC's runtime formats: bare major (<c>15</c>) or Major.Minor
    /// (<c>15.2</c>).
    /// </summary>
    private static readonly Regex RuntimeFormatRegex = new(@"^\d+(\.\d+)?$", RegexOptions.Compiled);

    private const string PendingMessage =
        "TemplateService write paths have not been migrated to the unified-extensions schema. " +
        "See Issue #54 follow-up.";

    /// <summary>
    /// Parses a Runtime string (e.g. <c>"15"</c> or <c>"15.2"</c>) into a
    /// sortable tuple so <c>9 &lt; 15 &lt; 15.2 &lt; 16</c> the way an admin
    /// expects.
    /// </summary>
    public static (int Major, int Minor) RuntimeSortKey(string? runtime)
    {
        if (string.IsNullOrWhiteSpace(runtime)) return (-1, -1);
        var parts = runtime.Trim().Split('.', 2);
        var major = int.TryParse(parts[0], out var m) ? m : -1;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
        return (major, minor);
    }

    private readonly AppDbContext _db;
    private readonly ILogger<TemplateService> _logger;
    private readonly IOrganizationContext _orgContext;

    public TemplateService(AppDbContext db, ILogger<TemplateService> logger, IOrganizationContext orgContext)
    {
        _db = db;
        _logger = logger;
        _orgContext = orgContext;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; service mutation called outside an authenticated request.");

    public async Task<List<RuntimeTemplate>> GetTemplatesAsync(bool includeDeprecated = true, CancellationToken ct = default)
    {
        var query = _db.RuntimeTemplates
            .AsNoTracking()
            .Where(t => t.DeletedAt == null);

        if (!includeDeprecated)
            query = query.Where(t => !t.Deprecated);

        var rows = await query
            .Include(t => t.WorkspaceExtensions.OrderBy(e => e.Ordering))
            .Include(t => t.DefaultModules.OrderBy(d => d.Ordering))
                .ThenInclude(d => d.Module!)
            .Include(t => t.DefaultApplicationVersion)
            .ToListAsync(ct);

        return rows
            .OrderBy(t => RuntimeSortKey(t.Runtime))
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<List<RuntimeTemplate>> GetAllForAdminAsync(bool includeDeleted, CancellationToken ct = default)
    {
        var query = _db.RuntimeTemplates.AsNoTracking();
        if (!includeDeleted)
        {
            query = query.Where(t => t.DeletedAt == null);
        }

        var rows = await query
            .Include(t => t.WorkspaceExtensions.OrderBy(e => e.Ordering))
            .Include(t => t.DefaultModules.OrderBy(d => d.Ordering))
                .ThenInclude(d => d.Module!)
            .Include(t => t.DefaultApplicationVersion)
            .ToListAsync(ct);

        return rows
            .OrderBy(t => t.DeletedAt == null ? 0 : 1)
            .ThenBy(t => RuntimeSortKey(t.Runtime))
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task<RuntimeTemplate?> GetDefaultAsync(CancellationToken ct = default)
    {
        return _db.RuntimeTemplates
            .AsNoTracking()
            .Where(t => t.IsDefault && t.DeletedAt == null && !t.Deprecated)
            .FirstOrDefaultAsync(ct);
    }

    public Task<RuntimeTemplate?> GetByKeyAsync(string key, CancellationToken ct = default)
    {
        return _db.RuntimeTemplates
            .AsNoTracking()
            .Where(t => t.Key == key)
            .Include(t => t.WorkspaceExtensions.OrderBy(e => e.Ordering))
            .Include(t => t.DefaultModules.OrderBy(d => d.Ordering))
                .ThenInclude(d => d.Module!)
            .Include(t => t.DefaultApplicationVersion)
            .FirstOrDefaultAsync(ct);
    }

    public Task<List<Module>> GetModulesAsync(bool includeDeprecated = true, CancellationToken ct = default)
    {
        var query = _db.Modules
            .AsNoTracking()
            .Where(m => m.DeletedAt == null);

        if (!includeDeprecated)
            query = query.Where(m => !m.Deprecated);

        return query
            .OrderBy(m => m.Name)
            .Include(m => m.Dependencies.OrderBy(d => d.Ordering))
            .ToListAsync(ct);
    }

    public Task<List<WellKnownDependency>> GetCatalogAsync(CancellationToken ct = default)
    {
        return _db.WellKnownDependencies
            .AsNoTracking()
            .OrderBy(w => w.Category)
            .ThenBy(w => w.Ordering)
            .ThenBy(w => w.DepName)
            .ToListAsync(ct);
    }

    public Task<RuntimeTemplate> CreateAsync(TemplateInput input, CancellationToken ct = default) =>
        throw new NotImplementedException(PendingMessage);

    public Task UpdateAsync(int id, TemplateInput input, CancellationToken ct = default) =>
        throw new NotImplementedException(PendingMessage);

    public async Task SetDefaultAsync(int id, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var target = await _db.RuntimeTemplates.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Template with id {id} was not found.",
            });

        if (target.DeletedAt is not null)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = "A soft-deleted template cannot be the default. Restore it first.",
            });
        }
        if (target.Deprecated)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = "A deprecated template cannot be the default. Un-deprecate it first.",
            });
        }

        if (target.IsDefault)
        {
            return;
        }

        var previous = await _db.RuntimeTemplates
            .Where(t => t.OrganizationId == orgId && t.IsDefault)
            .ToListAsync(ct);
        if (previous.Count > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var row in previous)
            {
                row.IsDefault = false;
                row.UpdatedAt = now;
            }
            await _db.SaveChangesAsync(ct);
        }

        target.IsDefault = true;
        target.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Set default runtime template to '{Key}' (id={Id}) for org {OrgId}.",
            target.Key, target.Id, orgId);
    }

    public async Task SoftDeleteAsync(int id, CancellationToken ct = default)
    {
        var existing = await _db.RuntimeTemplates.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Template with id {id} was not found.",
            });

        if (existing.DeletedAt is not null)
        {
            return;
        }

        existing.DeletedAt = DateTime.UtcNow;
        existing.UpdatedAt = existing.DeletedAt.Value;
        existing.IsDefault = false;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Soft-deleted runtime template '{Key}' (id={Id}).", existing.Key, existing.Id);
    }

    public async Task RestoreAsync(int id, CancellationToken ct = default)
    {
        var existing = await _db.RuntimeTemplates.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Template with id {id} was not found.",
            });

        if (existing.DeletedAt is null)
        {
            return;
        }

        existing.DeletedAt = null;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Restored runtime template '{Key}' (id={Id}).", existing.Key, existing.Id);
    }

    public Task<BulkActionResult> BulkSoftDeleteAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
        => BulkMutateAsync(ids, t =>
        {
            if (t.DeletedAt is not null) return false;
            t.DeletedAt = DateTime.UtcNow;
            t.UpdatedAt = t.DeletedAt.Value;
            t.IsDefault = false;
            return true;
        }, "soft-delete", ct);

    public Task<BulkActionResult> BulkRestoreAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
        => BulkMutateAsync(ids, t =>
        {
            if (t.DeletedAt is null) return false;
            t.DeletedAt = null;
            t.UpdatedAt = DateTime.UtcNow;
            return true;
        }, "restore", ct);

    public Task<BulkActionResult> BulkDeprecateAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
        => BulkMutateAsync(ids, t =>
        {
            if (t.Deprecated) return false;
            t.Deprecated = true;
            t.UpdatedAt = DateTime.UtcNow;
            t.IsDefault = false;
            return true;
        }, "deprecate", ct);

    public Task<BulkActionResult> BulkUnDeprecateAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
        => BulkMutateAsync(ids, t =>
        {
            if (!t.Deprecated) return false;
            t.Deprecated = false;
            t.UpdatedAt = DateTime.UtcNow;
            return true;
        }, "un-deprecate", ct);

    private async Task<BulkActionResult> BulkMutateAsync(
        IReadOnlyList<int> ids,
        Func<RuntimeTemplate, bool> mutate,
        string actionLabel,
        CancellationToken ct)
    {
        RequireOrganizationId();
        var succeeded = new List<int>();
        var failures = new List<BulkActionFailure>();
        foreach (var id in ids.Distinct())
        {
            var row = await _db.RuntimeTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (row is null)
            {
                failures.Add(new BulkActionFailure(id, $"#{id}", "Not found in this organisation."));
                continue;
            }
            try
            {
                if (!mutate(row))
                {
                    succeeded.Add(id);
                    continue;
                }
                await _db.SaveChangesAsync(ct);
                succeeded.Add(id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Bulk {Action} failed for template id={Id}.", actionLabel, id);
                failures.Add(new BulkActionFailure(id, row.Name, ex.Message));
                _db.Entry(row).State = EntityState.Detached;
            }
        }
        _logger.LogInformation(
            "Bulk {Action} on templates: {Ok}/{Total} succeeded.",
            actionLabel, succeeded.Count, ids.Count);
        return new BulkActionResult(ids.Count, succeeded, failures);
    }
}

/// <summary>
/// Form-shaped admin input for create/update operations. The two JSON columns
/// are accepted as raw strings so the textarea-based editor in
/// <c>/admin/templates/{key}</c> can hand them through unmodified.
/// </summary>
/// <remarks>
/// <see cref="Folders"/> and <see cref="ModuleFolders"/> are retained as
/// flat-path collections during the Issue #54 transition so the admin form
/// compiles against the existing markup. They are not currently persisted —
/// the write-side rewrite around <c>WorkspaceExtension</c> is pending.
/// </remarks>
public record TemplateInput(
    string Key,
    string Runtime,
    string Name,
    string? Description,
    string DefaultApplication,
    string DefaultPlatform,
    string DefaultsJson,
    string AppSourceCopJson,
    int CoreIdRangeFrom,
    int CoreIdRangeTo,
    int ModuleIdRangeStart,
    int ModuleIdRangeSize,
    bool Deprecated,
    IReadOnlyList<string> DefaultModuleKeys,
    IReadOnlyList<TemplateFolderInput> Folders,
    IReadOnlyList<TemplateFolderInput> ModuleFolders,
    string? DefaultApplicationVersionKey = null);

/// <summary>Legacy flat-path folder input retained for the unified-extensions transition.</summary>
public record TemplateFolderInput(string Path, IReadOnlyList<TemplateFileInput> Files);

/// <summary>Legacy file input retained for the unified-extensions transition.</summary>
public record TemplateFileInput(string Path, string Content);
