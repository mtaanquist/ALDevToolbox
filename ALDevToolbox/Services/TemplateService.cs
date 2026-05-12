using System.Text.Json;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Read- and write-side service for runtime templates under the
/// unified-extensions model. Drives the user-facing dropdowns and templates
/// browser; backs the <c>/admin/templates*</c> pages.
/// </summary>
/// <remarks>
/// Write side accepts <see cref="TemplateAuthoring"/> (produced by
/// <see cref="TemplateTomlMapper.FromToml(string, bool)"/>). The legacy
/// <see cref="TemplateInput"/> overloads still exist so the structured admin
/// form keeps compiling, but they throw <see cref="NotImplementedException"/>
/// — the form-editor rewrite around the recursive folder tree is a follow-on
/// PR. The TOML pane is the working authoring path for the unified model.
/// </remarks>
public class TemplateService
{
    /// <summary>Accepts BC's runtime formats: bare major (<c>15</c>) or Major.Minor (<c>15.2</c>).</summary>
    private static readonly Regex RuntimeFormatRegex = new(@"^\d+(\.\d+)?$", RegexOptions.Compiled);

    /// <summary>
    /// Valid path-segment for a folder or file. No slashes, no <c>..</c>, no
    /// leading/trailing whitespace, non-empty. Used for every recursive
    /// <c>workspace_extension_folders.path</c> and
    /// <c>workspace_extension_files.path</c>.
    /// </summary>
    private static readonly Regex PathSegmentRegex = new(@"^[^/\\\s][^/\\]*[^/\\\s]$|^[^/\\\s]$", RegexOptions.Compiled);

    /// <summary>
    /// Valid extension <c>path</c> (the stable identifier and ZIP folder name).
    /// Letters / digits / hyphens / underscores, must start with a letter.
    /// Matches the convention in <c>.design/unified-extensions.md</c> sample
    /// templates (<c>Core</c>, <c>Hotfix</c>, <c>document-capture</c>).
    /// </summary>
    private static readonly Regex ExtensionPathRegex = new(@"^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.Compiled);

    private const string LegacyInputMessage =
        "The structured admin form bridge to the unified-extensions schema is pending. " +
        "Author templates via the TOML editor until the form-editor rewrite lands.";

    private static readonly JsonSerializerOptions JsonOptions = PersistenceJson.Options;

    /// <summary>
    /// Parses a Runtime string (e.g. <c>"15"</c> or <c>"15.2"</c>) into a sortable tuple.
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

    // ===== Read side =====

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

    public Task<RuntimeTemplate?> GetDefaultAsync(CancellationToken ct = default) =>
        _db.RuntimeTemplates
            .AsNoTracking()
            .Where(t => t.IsDefault && t.DeletedAt == null && !t.Deprecated)
            .FirstOrDefaultAsync(ct);

    public Task<RuntimeTemplate?> GetByKeyAsync(string key, CancellationToken ct = default) =>
        _db.RuntimeTemplates
            .AsNoTracking()
            .Where(t => t.Key == key)
            .Include(t => t.WorkspaceExtensions.OrderBy(e => e.Ordering))
            .Include(t => t.DefaultModules.OrderBy(d => d.Ordering))
                .ThenInclude(d => d.Module!)
            .Include(t => t.DefaultApplicationVersion)
            .FirstOrDefaultAsync(ct);

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

    public Task<List<WellKnownDependency>> GetCatalogAsync(CancellationToken ct = default) =>
        _db.WellKnownDependencies
            .AsNoTracking()
            .OrderBy(w => w.Category)
            .ThenBy(w => w.Ordering)
            .ThenBy(w => w.DepName)
            .ToListAsync(ct);

    // ===== Write side =====

    /// <summary>
    /// Creates a new runtime template from a TOML authoring payload.
    /// Validation errors are thrown as <see cref="PlanValidationException"/>
    /// with field-keyed messages.
    /// </summary>
    public async Task<RuntimeTemplate> CreateAsync(TemplateAuthoring input, CancellationToken ct = default)
    {
        var (defaults, appSourceCop, defaultModuleIds, appVersionId) =
            await ValidateAsync(input, existingId: null, ct);

        var now = DateTime.UtcNow;
        var orgId = RequireOrganizationId();
        var template = new RuntimeTemplate
        {
            OrganizationId = orgId,
            Key = input.Key.Trim(),
            Runtime = input.Runtime.Trim(),
            Name = input.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
            Defaults = defaults,
            AppSourceCop = appSourceCop,
            CoreIdRangeFrom = input.CoreIdRangeFrom,
            CoreIdRangeTo = input.CoreIdRangeTo,
            ModuleIdRangeStart = input.ModuleIdRangeStart,
            ModuleIdRangeSize = input.ModuleIdRangeSize,
            Deprecated = input.Deprecated,
            DefaultApplicationVersionId = appVersionId,
            CreatedAt = now,
            UpdatedAt = now,
            WorkspaceExtensions = input.Extensions
                .Select((e, i) => BuildExtension(e, orgId, ordering: i))
                .ToList(),
            DefaultModules = defaultModuleIds
                .Select((moduleId, i) => new RuntimeTemplateDefaultModule
                {
                    OrganizationId = orgId,
                    ModuleId = moduleId,
                    Ordering = i,
                })
                .ToList(),
        };

        _db.RuntimeTemplates.Add(template);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Created runtime template '{Key}' (id={Id}) with {Extensions} extension(s).",
            template.Key, template.Id, template.WorkspaceExtensions.Count);
        return template;
    }

    /// <summary>
    /// Updates an existing template. Extensions, their folder trees, files,
    /// and dependencies are rebuilt from the authoring payload: the existing
    /// child rows are cascade-deleted and fresh rows take their place. Stable
    /// rows on the parent template (key, deprecated, default-modules join)
    /// are reconciled in place so their primary keys survive — the audit log
    /// stays compact for unchanged metadata edits.
    /// </summary>
    public async Task UpdateAsync(int id, TemplateAuthoring input, CancellationToken ct = default)
    {
        var existing = await _db.RuntimeTemplates
            .Include(t => t.WorkspaceExtensions)
                .ThenInclude(e => e.Dependencies)
            .Include(t => t.DefaultModules)
            .FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Template with id {id} was not found.",
            });

        // Key is immutable after creation — ignore whatever the input carries.
        var validatable = input with { Key = existing.Key };
        var (defaults, appSourceCop, defaultModuleIds, appVersionId) =
            await ValidateAsync(validatable, existingId: id, ct);

        existing.Runtime = input.Runtime.Trim();
        existing.Name = input.Name.Trim();
        existing.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        existing.Defaults = defaults;
        existing.AppSourceCop = appSourceCop;
        existing.CoreIdRangeFrom = input.CoreIdRangeFrom;
        existing.CoreIdRangeTo = input.CoreIdRangeTo;
        existing.ModuleIdRangeStart = input.ModuleIdRangeStart;
        existing.ModuleIdRangeSize = input.ModuleIdRangeSize;
        existing.Deprecated = input.Deprecated;
        existing.DefaultApplicationVersionId = appVersionId;
        existing.DefaultApplicationVersion = null;
        existing.UpdatedAt = DateTime.UtcNow;

        // Extensions: clear-and-rebuild. The cascade FK on workspace_extensions
        // drops all dependent folder / file / dep rows automatically. A
        // path-keyed reconciliation pass (preserving extension primary keys
        // for unchanged rows) is a possible refinement once the audit log
        // pattern around recursive trees firms up — for now, simplicity wins.
        existing.WorkspaceExtensions.Clear();
        var orgId = existing.OrganizationId;
        var fresh = input.Extensions
            .Select((e, i) => BuildExtension(e, orgId, ordering: i))
            .ToList();
        foreach (var ext in fresh) existing.WorkspaceExtensions.Add(ext);

        ReconcileDefaultModules(existing, defaultModuleIds, orgId);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Updated runtime template '{Key}' (id={Id}); now has {Extensions} extension(s).",
            existing.Key, existing.Id, existing.WorkspaceExtensions.Count);
    }

    // ===== Builders =====

    private static WorkspaceExtension BuildExtension(ExtensionAuthoring src, int orgId, int ordering) => new()
    {
        OrganizationId = orgId,
        Ordering = ordering,
        Path = src.Path.Trim(),
        NameTemplate = src.NameTemplate.Trim(),
        Required = src.Required,
        Application = string.IsNullOrEmpty(src.Application) ? null : src.Application.Trim(),
        Runtime = string.IsNullOrEmpty(src.Runtime) ? null : src.Runtime.Trim(),
        IdRangeFrom = src.IdRangeFrom,
        IdRangeTo = src.IdRangeTo,
        Folders = src.Folders
            .Select((f, i) => BuildFolder(f, orgId, ordering: i))
            .ToList(),
        Dependencies = src.Dependencies
            .Select((d, i) => BuildDependency(d, orgId, ordering: i))
            .ToList(),
    };

    private static WorkspaceExtensionFolder BuildFolder(FolderAuthoring src, int orgId, int ordering) => new()
    {
        OrganizationId = orgId,
        Ordering = ordering,
        Path = src.Path.Trim(),
        Files = src.Files
            .Select((f, i) => new WorkspaceExtensionFile
            {
                OrganizationId = orgId,
                Ordering = i,
                Path = f.Path.Trim(),
                Content = f.Content ?? string.Empty,
                IsExample = f.IsExample,
            })
            .ToList(),
        Folders = src.Folders
            .Select((f, i) => BuildFolder(f, orgId, ordering: i))
            .ToList(),
    };

    private static WorkspaceExtensionDependency BuildDependency(DependencyAuthoring src, int orgId, int ordering) => new()
    {
        OrganizationId = orgId,
        Ordering = ordering,
        RefExtensionPath = src.RefExtensionPath?.Trim(),
        RefModuleKey = src.RefModuleKey?.Trim(),
        LitId = src.LitId?.Trim(),
        LitName = src.LitName?.Trim(),
        LitPublisher = src.LitPublisher?.Trim(),
        LitVersion = src.LitVersion?.Trim(),
    };

    /// <summary>
    /// Reconciles the join rows for the per-org default-modules list. Matches
    /// by <see cref="RuntimeTemplateDefaultModule.ModuleId"/> rather than by
    /// list position: each row's natural identity is the module it points
    /// at, so reordering the input list only rewrites <c>Ordering</c> and
    /// the join row primary keys survive. Crucially this avoids the swap
    /// cycle — mutating two rows' <c>module_id</c> in one batch would trip
    /// the <c>(runtime_template_id, module_id)</c> unique index, which EF's
    /// command-batcher detects and refuses to topologically sort.
    /// </summary>
    private static void ReconcileDefaultModules(RuntimeTemplate existing, IReadOnlyList<int> moduleIds, int orgId)
    {
        var existingByModuleId = existing.DefaultModules.ToDictionary(d => d.ModuleId);

        for (var i = 0; i < moduleIds.Count; i++)
        {
            var moduleId = moduleIds[i];
            if (existingByModuleId.TryGetValue(moduleId, out var row))
            {
                // Keep the PK; rewrite ordering only when it actually changed.
                if (row.Ordering != i) row.Ordering = i;
                row.Module = null;
            }
            else
            {
                existing.DefaultModules.Add(new RuntimeTemplateDefaultModule
                {
                    OrganizationId = orgId,
                    Ordering = i,
                    ModuleId = moduleId,
                });
            }
        }

        // Remove rows whose ModuleId fell off the input list.
        var keep = new HashSet<int>(moduleIds);
        var toRemove = existing.DefaultModules.Where(d => !keep.Contains(d.ModuleId)).ToList();
        foreach (var row in toRemove)
        {
            existing.DefaultModules.Remove(row);
        }
    }

    // ===== Default ops =====

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

        if (target.IsDefault) return;

        // Two-phase update so the filtered unique index never sees two true rows.
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

        if (existing.DeletedAt is not null) return;

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

        if (existing.DeletedAt is null) return;

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
                _logger.LogWarning(ex, "Bulk {Action} failed for template id={Id}.", actionLabel, id);
                failures.Add(new BulkActionFailure(id, row.Name, ex.Message));
                _db.Entry(row).State = EntityState.Detached;
            }
        }
        _logger.LogInformation(
            "Bulk {Action} on templates: {Ok}/{Total} succeeded.",
            actionLabel, succeeded.Count, ids.Count);
        return new BulkActionResult(ids.Count, succeeded, failures);
    }

    // ===== Validation =====

    /// <summary>
    /// Validates the authoring payload. Returns the parsed value objects and
    /// resolved FK ids so the caller doesn't re-deserialise. Throws an
    /// aggregated <see cref="PlanValidationException"/> with one entry per
    /// problem so the editor can highlight everything at once.
    /// </summary>
    private async Task<(TemplateDefaults Defaults, AppSourceCopSettings AppSourceCop, IReadOnlyList<int> DefaultModuleIds, int? DefaultApplicationVersionId)>
        ValidateAsync(TemplateAuthoring input, int? existingId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();

        var key = input.Key?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(key))
        {
            errors[nameof(input.Key)] = "Key is required.";
        }
        else if (!ValidationPatterns.Key.IsMatch(key))
        {
            errors[nameof(input.Key)] = "Key must contain only lowercase letters, digits, and hyphens.";
        }
        else
        {
            var clash = await _db.RuntimeTemplates
                .AsNoTracking()
                .Where(t => t.Key == key)
                .Select(t => (int?)t.Id)
                .FirstOrDefaultAsync(ct);
            if (clash is not null && clash != existingId)
            {
                errors[nameof(input.Key)] = $"A template with key '{key}' already exists.";
            }
        }

        if (string.IsNullOrWhiteSpace(input.Runtime))
        {
            errors[nameof(input.Runtime)] = "Runtime is required.";
        }
        else if (!RuntimeFormatRegex.IsMatch(input.Runtime.Trim()))
        {
            errors[nameof(input.Runtime)] = "Runtime must be a number, optionally with one minor part (e.g. 15 or 15.2).";
        }

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            errors[nameof(input.Name)] = "Name is required.";
        }

        if (input.CoreIdRangeFrom <= 0) errors[nameof(input.CoreIdRangeFrom)] = "Core ID range start must be greater than zero.";
        if (input.CoreIdRangeTo <= input.CoreIdRangeFrom) errors[nameof(input.CoreIdRangeTo)] = "Core ID range end must be greater than the start.";
        if (input.ModuleIdRangeStart <= 0) errors[nameof(input.ModuleIdRangeStart)] = "Module ID range start must be greater than zero.";
        if (input.ModuleIdRangeSize <= 0) errors[nameof(input.ModuleIdRangeSize)] = "Module ID range size must be greater than zero.";

        TemplateDefaults defaults = new();
        try
        {
            defaults = string.IsNullOrWhiteSpace(input.DefaultsJson)
                ? new TemplateDefaults()
                : JsonSerializer.Deserialize<TemplateDefaults>(input.DefaultsJson, JsonOptions) ?? new TemplateDefaults();
        }
        catch (JsonException ex)
        {
            errors[nameof(input.DefaultsJson)] = $"Defaults JSON is invalid: {ex.Message}";
        }

        AppSourceCopSettings appSourceCop = new();
        try
        {
            appSourceCop = string.IsNullOrWhiteSpace(input.AppSourceCopJson)
                ? new AppSourceCopSettings()
                : JsonSerializer.Deserialize<AppSourceCopSettings>(input.AppSourceCopJson, JsonOptions) ?? new AppSourceCopSettings();
        }
        catch (JsonException ex)
        {
            errors[nameof(input.AppSourceCopJson)] = $"AppSourceCop JSON is invalid: {ex.Message}";
        }

        ValidateExtensions(input.Extensions, errors);

        // Default modules: resolve to ids, refuse unknown or soft-deleted keys.
        var defaultModuleIds = new List<int>();
        if (input.DefaultModuleKeys.Count > 0)
        {
            var trimmed = input.DefaultModuleKeys
                .Select(k => k?.Trim() ?? string.Empty)
                .Where(k => !string.IsNullOrEmpty(k))
                .ToList();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var orderedUnique = new List<string>();
            foreach (var k in trimmed)
            {
                if (seen.Add(k)) orderedUnique.Add(k);
            }
            var matched = await _db.Modules
                .AsNoTracking()
                .Where(m => orderedUnique.Contains(m.Key) && m.DeletedAt == null)
                .Select(m => new { m.Key, m.Id })
                .ToListAsync(ct);
            var idByKey = matched.ToDictionary(m => m.Key, m => m.Id, StringComparer.Ordinal);

            var missing = orderedUnique.Where(k => !idByKey.ContainsKey(k)).ToList();
            if (missing.Count > 0)
            {
                errors[nameof(input.DefaultModuleKeys)] = $"Unknown default module(s): {string.Join(", ", missing)}.";
            }
            else
            {
                defaultModuleIds = orderedUnique.Select(k => idByKey[k]).ToList();
            }
        }

        int? defaultApplicationVersionId = null;
        var versionKey = input.DefaultApplicationVersionKey?.Trim();
        if (!string.IsNullOrEmpty(versionKey))
        {
            var resolved = await _db.ApplicationVersions
                .AsNoTracking()
                .Where(a => a.Key == versionKey && a.DeletedAt == null)
                .Select(a => (int?)a.Id)
                .FirstOrDefaultAsync(ct);
            if (resolved is null)
            {
                errors[nameof(input.DefaultApplicationVersionKey)] = $"Unknown application version '{versionKey}'.";
            }
            else
            {
                defaultApplicationVersionId = resolved;
            }
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);
        return (defaults, appSourceCop, defaultModuleIds, defaultApplicationVersionId);
    }

    /// <summary>
    /// Walks every <c>[[extensions]]</c> entry and checks: extension <c>path</c>
    /// non-empty + unique + filesystem-safe; <c>name</c> template non-empty;
    /// id-range pair both-or-neither; recursive folder/file paths are
    /// single-segment + sibling-unique; each dependency sets exactly one
    /// reference shape, and any intra-template extension ref resolves to
    /// another path in this template.
    /// </summary>
    private static void ValidateExtensions(IReadOnlyList<ExtensionAuthoring> extensions, IDictionary<string, string> errors)
    {
        var pathsSeen = new HashSet<string>(StringComparer.Ordinal);
        var pathsCaseInsensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < extensions.Count; i++)
        {
            var ext = extensions[i];
            var prefix = $"Extensions[{i}]";

            var path = ext.Path?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(path))
            {
                errors[$"{prefix}.Path"] = "Extension path is required.";
            }
            else if (!ExtensionPathRegex.IsMatch(path))
            {
                errors[$"{prefix}.Path"] = "Extension path must start with a letter and contain only letters, digits, hyphens, or underscores.";
            }
            else if (!pathsSeen.Add(path))
            {
                errors[$"{prefix}.Path"] = $"Duplicate extension path '{path}'.";
            }
            else if (!pathsCaseInsensitive.Add(path))
            {
                errors[$"{prefix}.Path"] = $"Extension path '{path}' collides case-insensitively with another extension. Windows treats them as the same folder.";
            }

            if (string.IsNullOrWhiteSpace(ext.NameTemplate))
            {
                errors[$"{prefix}.NameTemplate"] = "Extension name template is required.";
            }

            // Both id-range bounds must be set together (or both omitted).
            // Half-set ranges would silently break the generator's auto-allocator.
            if (ext.IdRangeFrom is int from && ext.IdRangeTo is int to)
            {
                if (from <= 0) errors[$"{prefix}.IdRangeFrom"] = "Id range start must be greater than zero.";
                if (to <= from) errors[$"{prefix}.IdRangeTo"] = "Id range end must be greater than 'from'.";
            }
            else if (ext.IdRangeFrom is not null || ext.IdRangeTo is not null)
            {
                errors[$"{prefix}.IdRange"] = "Set both id_range_from and id_range_to, or neither.";
            }

            ValidateFolderTree(ext.Folders, prefix + ".Folders", errors);
            ValidateDependencies(ext.Dependencies, prefix + ".Dependencies", pathsSeen, errors);
        }
    }

    private static void ValidateFolderTree(IReadOnlyList<FolderAuthoring> folders, string prefix, IDictionary<string, string> errors)
    {
        var siblingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < folders.Count; i++)
        {
            var folder = folders[i];
            var folderPrefix = $"{prefix}[{i}]";
            var path = folder.Path?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(path))
            {
                errors[$"{folderPrefix}.Path"] = "Folder path is required.";
            }
            else if (!PathSegmentRegex.IsMatch(path) || path == "." || path == "..")
            {
                errors[$"{folderPrefix}.Path"] =
                    "Folder path must be a single segment — no slashes, no '..', no leading/trailing whitespace.";
            }
            else if (!siblingPaths.Add(path))
            {
                errors[$"{folderPrefix}.Path"] = $"Duplicate sibling folder '{path}' (case-insensitive).";
            }

            var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var f = 0; f < folder.Files.Count; f++)
            {
                var file = folder.Files[f];
                var filePrefix = $"{folderPrefix}.Files[{f}]";
                var filePath = file.Path?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(filePath))
                {
                    errors[$"{filePrefix}.Path"] = "File path is required.";
                }
                else if (!PathSegmentRegex.IsMatch(filePath) || filePath == "." || filePath == "..")
                {
                    errors[$"{filePrefix}.Path"] =
                        "File path must be a basename — no slashes, no '..', no leading/trailing whitespace.";
                }
                else if (!fileNames.Add(filePath))
                {
                    errors[$"{filePrefix}.Path"] = $"Duplicate file '{filePath}' in this folder (case-insensitive).";
                }
            }

            ValidateFolderTree(folder.Folders, folderPrefix + ".Folders", errors);
        }
    }

    private static void ValidateDependencies(
        IReadOnlyList<DependencyAuthoring> deps,
        string prefix,
        IReadOnlySet<string> knownExtensionPaths,
        IDictionary<string, string> errors)
    {
        for (var i = 0; i < deps.Count; i++)
        {
            var dep = deps[i];
            var depPrefix = $"{prefix}[{i}]";

            // Exactly one of the three reference shapes must be set. The DB
            // CHECK constraint enforces this too, but field-keyed messages are
            // friendlier than a Postgres error string in the editor.
            var setCount = (dep.RefExtensionPath is not null ? 1 : 0)
                + (dep.RefModuleKey is not null ? 1 : 0)
                + (dep.LitId is not null ? 1 : 0);
            if (setCount == 0)
            {
                errors[depPrefix] = "Each dependency must set one of: extension, module, or id.";
                continue;
            }
            if (setCount > 1)
            {
                errors[depPrefix] = "A dependency must use only one of: extension, module, or id (not several).";
                continue;
            }

            if (dep.RefExtensionPath is string refPath
                && !knownExtensionPaths.Contains(refPath))
            {
                errors[$"{depPrefix}.Extension"] =
                    $"Dependency references extension '{refPath}', which isn't declared by this template.";
            }
            else if (dep.LitId is string litId)
            {
                // Lightweight GUID sanity check — the dep_id field is otherwise
                // free-form because AL accepts wrapped {GUID} and bare forms.
                if (litId.Length < 4)
                {
                    errors[$"{depPrefix}.Id"] = "Literal dependency id is too short.";
                }
                if (string.IsNullOrWhiteSpace(dep.LitName))
                {
                    errors[$"{depPrefix}.Name"] = "Literal dependency name is required.";
                }
                if (string.IsNullOrWhiteSpace(dep.LitPublisher))
                {
                    errors[$"{depPrefix}.Publisher"] = "Literal dependency publisher is required.";
                }
                if (string.IsNullOrWhiteSpace(dep.LitVersion))
                {
                    errors[$"{depPrefix}.Version"] = "Literal dependency version is required.";
                }
            }
        }
    }

    // ===== Legacy form-binding stubs =====

    /// <summary>
    /// Legacy structured-form input. Kept for compile compatibility with the
    /// unmigrated admin page; the form-editor rewrite around the recursive
    /// folder tree is a follow-on PR. Both overloads throw at runtime.
    /// </summary>
    public Task<RuntimeTemplate> CreateAsync(TemplateInput input, CancellationToken ct = default) =>
        throw new NotImplementedException(LegacyInputMessage);

    public Task UpdateAsync(int id, TemplateInput input, CancellationToken ct = default) =>
        throw new NotImplementedException(LegacyInputMessage);
}

/// <summary>
/// Form-shaped admin input for create/update operations under the legacy
/// structured editor. The unified-extensions rewrite of the editor is a
/// follow-on PR; this record stays in place so the form's <c>ToInput()</c>
/// keeps compiling. Routed through the throwing overloads above.
/// </summary>
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

/// <summary>Legacy flat-path folder input retained for the structured-form transition.</summary>
public record TemplateFolderInput(string Path, IReadOnlyList<TemplateFileInput> Files);

/// <summary>Legacy file input retained for the structured-form transition.</summary>
public record TemplateFileInput(string Path, string Content);
