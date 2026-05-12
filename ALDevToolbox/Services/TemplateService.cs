using System.Text.Json;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Read- and write-side service for runtime templates. The query helpers drive
/// the user-facing dropdowns and the templates browser; the admin CRUD methods
/// back the <c>/admin/templates*</c> pages and enforce the domain rules from
/// <c>.design/domain-model.md</c>.
/// </summary>
public class TemplateService
{
    /// <summary>
    /// Accepts BC's runtime formats: bare major (<c>15</c>) or
    /// Major.Minor (<c>15.2</c>). The seed schema and the admin form both
    /// post strings; the validation lives here so neither can sneak past it.
    /// </summary>
    private static readonly Regex RuntimeFormatRegex = new(@"^\d+(\.\d+)?$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = PersistenceJson.Options;

    /// <summary>
    /// Parses a Runtime string (e.g. <c>"15"</c> or <c>"15.2"</c>) into a
    /// sortable tuple so <c>9 &lt; 15 &lt; 15.2 &lt; 16</c> the way an admin
    /// expects, instead of the lexicographic order a TEXT column would
    /// otherwise give us. Unparseable values sort first.
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

    /// <summary>
    /// Resolves the acting user's organisation, throwing when no user is
    /// signed in. Service code that mutates state can never run without an
    /// org context (the endpoints that reach this service are
    /// <c>[Authorize]</c>'d).
    /// </summary>
    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; service mutation called outside an authenticated request.");

    /// <summary>
    /// Returns every active runtime template (i.e. not soft-deleted), ordered by
    /// runtime version. <paramref name="includeDeprecated"/> is <c>true</c> for
    /// admin views and <c>false</c> for end-user dropdowns.
    /// </summary>
    public async Task<List<RuntimeTemplate>> GetTemplatesAsync(bool includeDeprecated = true, CancellationToken ct = default)
    {
        var query = _db.RuntimeTemplates
            .AsNoTracking()
            .Where(t => t.DeletedAt == null);

        if (!includeDeprecated)
            query = query.Where(t => !t.Deprecated);

        // Sorting on Runtime happens after materialisation now: the column is
        // text and lexicographic ordering would put "10" before "9".
        // RuntimeSortKey gives admins the version-aware ordering they expect.
        var rows = await query
            .Include(t => t.Folders.OrderBy(f => f.Ordering))
                .ThenInclude(f => f.Files.OrderBy(x => x.Ordering))
            .Include(t => t.ModuleFolders.OrderBy(f => f.Ordering))
                .ThenInclude(f => f.Files.OrderBy(x => x.Ordering))
            .Include(t => t.DefaultModules.OrderBy(d => d.Ordering))
                .ThenInclude(d => d.Module!)
            .Include(t => t.DefaultApplicationVersion)
            .ToListAsync(ct);

        return rows
            .OrderBy(t => RuntimeSortKey(t.Runtime))
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns every template, including deprecated and soft-deleted ones. Drives
    /// the admin list view, where deleted rows are recoverable via "Restore".
    /// </summary>
    public async Task<List<RuntimeTemplate>> GetAllForAdminAsync(bool includeDeleted, CancellationToken ct = default)
    {
        var query = _db.RuntimeTemplates.AsNoTracking();
        if (!includeDeleted)
        {
            query = query.Where(t => t.DeletedAt == null);
        }

        var rows = await query
            .Include(t => t.Folders.OrderBy(f => f.Ordering))
                .ThenInclude(f => f.Files.OrderBy(x => x.Ordering))
            .Include(t => t.ModuleFolders.OrderBy(f => f.Ordering))
                .ThenInclude(f => f.Files.OrderBy(x => x.Ordering))
            .Include(t => t.DefaultModules.OrderBy(d => d.Ordering))
                .ThenInclude(d => d.Module!)
            .Include(t => t.DefaultApplicationVersion)
            .ToListAsync(ct);

        // Same trick as GetTemplatesAsync: keep version-aware ordering
        // client-side because the DB column is now TEXT.
        return rows
            .OrderBy(t => t.DeletedAt == null ? 0 : 1)
            .ThenBy(t => RuntimeSortKey(t.Runtime))
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns the org's default template if one is flagged and the row is
    /// still active and non-deprecated. The user-facing New Workspace / New
    /// Extension forms call this to preselect the dropdown when no explicit
    /// ?template= hint is supplied.
    /// </summary>
    public Task<RuntimeTemplate?> GetDefaultAsync(CancellationToken ct = default)
    {
        return _db.RuntimeTemplates
            .AsNoTracking()
            .Where(t => t.IsDefault && t.DeletedAt == null && !t.Deprecated)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Returns a single template by its <see cref="RuntimeTemplate.Key"/>, including
    /// soft-deleted rows so admin pages can render them. Returns <c>null</c> if no
    /// template has that key.
    /// </summary>
    public Task<RuntimeTemplate?> GetByKeyAsync(string key, CancellationToken ct = default)
    {
        return _db.RuntimeTemplates
            .AsNoTracking()
            .Where(t => t.Key == key)
            .Include(t => t.Folders.OrderBy(f => f.Ordering))
                .ThenInclude(f => f.Files.OrderBy(x => x.Ordering))
            .Include(t => t.ModuleFolders.OrderBy(f => f.Ordering))
                .ThenInclude(f => f.Files.OrderBy(x => x.Ordering))
            .Include(t => t.DefaultModules.OrderBy(d => d.Ordering))
                .ThenInclude(d => d.Module!)
            .Include(t => t.DefaultApplicationVersion)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Returns every active module, ordered by display name.
    /// </summary>
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

    /// <summary>
    /// Returns every well-known catalogue dependency, in display order. Drives
    /// the catalogue side of the New Extension dependency picker.
    /// </summary>
    public Task<List<WellKnownDependency>> GetCatalogAsync(CancellationToken ct = default)
    {
        return _db.WellKnownDependencies
            .AsNoTracking()
            .OrderBy(w => w.Category)
            .ThenBy(w => w.Ordering)
            .ThenBy(w => w.DepName)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Creates a new runtime template plus its folders. Validation errors are
    /// thrown as <see cref="PlanValidationException"/> with field-keyed messages
    /// so the form can render them inline.
    /// </summary>
    public async Task<RuntimeTemplate> CreateAsync(TemplateInput input, CancellationToken ct = default)
    {
        var (defaults, defaultModuleIds, defaultApplicationVersionId) =
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
            DefaultApplication = input.DefaultApplication.Trim(),
            DefaultPlatform = input.DefaultPlatform.Trim(),
            Defaults = defaults,
            CoreIdRangeFrom = input.CoreIdRangeFrom,
            CoreIdRangeTo = input.CoreIdRangeTo,
            ModuleIdRangeStart = input.ModuleIdRangeStart,
            ModuleIdRangeSize = input.ModuleIdRangeSize,
            Deprecated = input.Deprecated,
            DefaultApplicationVersionId = defaultApplicationVersionId,
            WorkspaceTemplate = input.WorkspaceTemplate ?? string.Empty,
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = null,
            Folders = input.Folders
                .Select((f, i) => new TemplateFolder
                {
                    OrganizationId = orgId,
                    Ordering = i,
                    Path = f.Path.Trim(),
                    Files = f.Files
                        .Select((file, fi) => new TemplateFile
                        {
                            OrganizationId = orgId,
                            Ordering = fi,
                            Path = file.Path.Trim(),
                            Content = file.Content ?? string.Empty,
                        })
                        .ToList(),
                })
                .ToList(),
            ModuleFolders = input.ModuleFolders
                .Select((f, i) => new TemplateModuleFolder
                {
                    OrganizationId = orgId,
                    Ordering = i,
                    Path = f.Path.Trim(),
                    Files = f.Files
                        .Select((file, fi) => new TemplateModuleFile
                        {
                            OrganizationId = orgId,
                            Ordering = fi,
                            Path = file.Path.Trim(),
                            Content = file.Content ?? string.Empty,
                        })
                        .ToList(),
                })
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
            "Created runtime template '{Key}' (id={Id}) with {FolderCount} folder(s).",
            template.Key, template.Id, template.Folders.Count);
        return template;
    }

    /// <summary>
    /// Updates an existing template's fields and reconciles its folder list.
    /// The <see cref="RuntimeTemplate.Key"/> is immutable after creation; the
    /// caller-supplied key on <paramref name="input"/> is ignored.
    /// </summary>
    public async Task UpdateAsync(int id, TemplateInput input, CancellationToken ct = default)
    {
        var existing = await _db.RuntimeTemplates
            .Include(t => t.Folders).ThenInclude(f => f.Files)
            .Include(t => t.ModuleFolders).ThenInclude(f => f.Files)
            .Include(t => t.DefaultModules)
            .FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Template with id {id} was not found.",
            });

        // Preserve the existing key for validation (keys can't change after creation).
        var validatableInput = input with { Key = existing.Key };
        var (defaults, defaultModuleIds, defaultApplicationVersionId) =
            await ValidateAsync(validatableInput, existingId: id, ct);

        existing.Runtime = input.Runtime.Trim();
        existing.Name = input.Name.Trim();
        existing.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        existing.DefaultApplication = input.DefaultApplication.Trim();
        existing.DefaultPlatform = input.DefaultPlatform.Trim();
        existing.Defaults = defaults;
        existing.CoreIdRangeFrom = input.CoreIdRangeFrom;
        existing.CoreIdRangeTo = input.CoreIdRangeTo;
        existing.ModuleIdRangeStart = input.ModuleIdRangeStart;
        existing.ModuleIdRangeSize = input.ModuleIdRangeSize;
        existing.Deprecated = input.Deprecated;
        existing.DefaultApplicationVersionId = defaultApplicationVersionId;
        existing.WorkspaceTemplate = input.WorkspaceTemplate ?? string.Empty;
        // Drop the cached navigation reference so EF doesn't get confused if a
        // previously-attached ApplicationVersion is still tracked.
        existing.DefaultApplicationVersion = null;
        existing.UpdatedAt = DateTime.UtcNow;

        ReconcileFolders(existing, input.Folders, existing.OrganizationId);
        ReconcileModuleFolders(existing, input.ModuleFolders, existing.OrganizationId);
        ReconcileDefaultModules(existing, defaultModuleIds, existing.OrganizationId);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Updated runtime template '{Key}' (id={Id}); now has {FolderCount} folder(s).",
            existing.Key, existing.Id, existing.Folders.Count);
    }

    /// <summary>
    /// Marks <paramref name="id"/> as the per-organisation default template. The
    /// previous default (if any) is cleared in the same SaveChanges so the
    /// filtered unique index never sees two true values at once. Throws when
    /// the target is missing, soft-deleted, or deprecated — only an active,
    /// visible template should preselect itself on the user-facing forms.
    /// </summary>
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

        // Postgres enforces the filtered unique index per statement (partial
        // unique indexes can't be DEFERRABLE), and EF doesn't guarantee the
        // order of the two UPDATEs inside a single SaveChanges. Clear the
        // previous default in its own round-trip first, then flip the new
        // one — so we can never have two true rows visible to the constraint
        // at the same time.
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

    /// <summary>
    /// Soft-deletes a template by setting <see cref="RuntimeTemplate.DeletedAt"/>.
    /// The row remains in the database so the admin can later <see cref="RestoreAsync"/>
    /// it. End-user dropdowns and the templates browser hide soft-deleted rows.
    /// </summary>
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
        // A soft-deleted template can never be the org default; the filtered
        // unique index requires it explicitly and the user-facing dropdown
        // hides deleted rows anyway.
        existing.IsDefault = false;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Soft-deleted runtime template '{Key}' (id={Id}).", existing.Key, existing.Id);
    }

    /// <summary>
    /// Clears <see cref="RuntimeTemplate.DeletedAt"/> on a previously soft-deleted
    /// template, making it visible to admin and (unless deprecated) end-user lists again.
    /// </summary>
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

    /// <summary>
    /// Bulk variant of <see cref="SoftDeleteAsync"/>: loops the supplied ids,
    /// soft-deleting each in turn and persisting between rows so the audit
    /// interceptor writes one row per entity (per
    /// <c>.design/milestones.md</c> Milestone 20). Failures are surfaced per
    /// row so a single tampered id can't halt the rest.
    /// </summary>
    public Task<BulkActionResult> BulkSoftDeleteAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
        => BulkMutateAsync(ids, t =>
        {
            if (t.DeletedAt is not null) return false;
            t.DeletedAt = DateTime.UtcNow;
            t.UpdatedAt = t.DeletedAt.Value;
            // Mirrors SoftDeleteAsync — a deleted template can't carry the
            // per-org default flag (filtered unique index requires it).
            t.IsDefault = false;
            return true;
        }, "soft-delete", ct);

    /// <summary>Bulk variant of <see cref="RestoreAsync"/>.</summary>
    public Task<BulkActionResult> BulkRestoreAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
        => BulkMutateAsync(ids, t =>
        {
            if (t.DeletedAt is null) return false;
            t.DeletedAt = null;
            t.UpdatedAt = DateTime.UtcNow;
            return true;
        }, "restore", ct);

    /// <summary>Bulk deprecate: flips <see cref="RuntimeTemplate.Deprecated"/> true.</summary>
    public Task<BulkActionResult> BulkDeprecateAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
        => BulkMutateAsync(ids, t =>
        {
            if (t.Deprecated) return false;
            t.Deprecated = true;
            t.UpdatedAt = DateTime.UtcNow;
            // Deprecated templates aren't user-selectable, so they shouldn't
            // carry the org default — clearing it here lets the admin set a
            // new default without first un-deprecating the old one.
            t.IsDefault = false;
            return true;
        }, "deprecate", ct);

    /// <summary>Bulk un-deprecate: clears <see cref="RuntimeTemplate.Deprecated"/>.</summary>
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
        // Touching the EF query filter is implicit: _db.RuntimeTemplates is
        // already scoped to the acting organisation, so an id from another
        // org just doesn't resolve.
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
                    // No-op — already in the target state. Treat as success
                    // so an admin re-running a bulk action sees a clean
                    // outcome instead of a noisy "nothing happened".
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
                // Detach the now-poisoned entry so the next iteration's
                // SaveChanges doesn't try to flush an aborted mutation.
                _db.Entry(row).State = EntityState.Detached;
            }
        }
        _logger.LogInformation(
            "Bulk {Action} on templates: {Ok}/{Total} succeeded.",
            actionLabel, succeeded.Count, ids.Count);
        return new BulkActionResult(ids.Count, succeeded, failures);
    }

    /// <summary>
    /// Walks the input folder list against the persisted folder list and applies
    /// the minimum set of mutations: update existing rows in-order, append new
    /// rows for any extras, remove rows that fell off the end. Per-folder
    /// <see cref="TemplateFile"/> rows are reconciled the same way. Keeps stable
    /// primary keys for unchanged rows so the audit log only captures real
    /// changes.
    /// </summary>
    private static void ReconcileFolders(RuntimeTemplate existing, IReadOnlyList<TemplateFolderInput> inputs, int orgId)
    {
        var existingFolders = existing.Folders.OrderBy(f => f.Ordering).ToList();

        for (var i = 0; i < inputs.Count; i++)
        {
            var inputFolder = inputs[i];
            var path = inputFolder.Path.Trim();

            TemplateFolder folder;
            if (i < existingFolders.Count)
            {
                folder = existingFolders[i];
                folder.Ordering = i;
                folder.Path = path;
            }
            else
            {
                folder = new TemplateFolder { OrganizationId = orgId, Ordering = i, Path = path };
                existing.Folders.Add(folder);
            }

            ReconcileFiles(folder, inputFolder.Files, orgId);
        }

        for (var i = inputs.Count; i < existingFolders.Count; i++)
        {
            existing.Folders.Remove(existingFolders[i]);
        }
    }

    /// <summary>
    /// Validates a folder-input collection (folders or module-folders) and
    /// records errors under <paramref name="fieldPrefix"/>. Both collections
    /// share the same path/uniqueness rules; only the field-key prefix
    /// differs so the form can render errors next to the right editor.
    /// </summary>
    private static void ValidateFolderInputs(
        IReadOnlyList<TemplateFolderInput> folders,
        string fieldPrefix,
        IDictionary<string, string> errors)
    {
        var seenFolderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < folders.Count; i++)
        {
            var folder = folders[i];
            var path = folder.Path?.Trim() ?? string.Empty;
            var fieldKey = $"{fieldPrefix}[{i}].Path";
            // Empty path is a deliberate value meaning "extension root" — the
            // folder's files land directly next to app.json. Validated via
            // the HashSet below (only one root row per list) rather than the
            // shape rules that apply to nested paths.
            if (path.Length > 0
                && (path.StartsWith('/') || path.Contains('\\')
                    || path.Split('/').Any(seg => seg == ".." || string.IsNullOrWhiteSpace(seg))))
            {
                errors[fieldKey] = "Folder path must be relative, use '/' separators, and contain no '..' segments.";
            }
            else if (!seenFolderPaths.Add(path))
            {
                errors[fieldKey] = path.Length == 0
                    ? "Only one extension-root folder (empty path) is allowed per list."
                    : $"Duplicate folder path '{path}' (case-insensitive). Windows treats these as the same folder.";
            }

            var seenFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var j = 0; j < folder.Files.Count; j++)
            {
                var file = folder.Files[j];
                var filePath = file.Path?.Trim() ?? string.Empty;
                var fileFieldKey = $"{fieldPrefix}[{i}].Files[{j}].Path";
                if (string.IsNullOrEmpty(filePath))
                {
                    errors[fileFieldKey] = "File path is required.";
                    continue;
                }
                if (filePath.StartsWith('/') || filePath.Contains('\\') || filePath.Split('/').Any(seg => seg == ".." || string.IsNullOrWhiteSpace(seg)))
                {
                    errors[fileFieldKey] = "File path must be relative, use '/' separators, and contain no '..' segments.";
                    continue;
                }
                if (!seenFilePaths.Add(filePath))
                {
                    errors[fileFieldKey] = $"Duplicate file path '{filePath}' (case-insensitive) inside this folder.";
                }
            }
        }
    }

    /// <summary>
    /// Same incremental update as <see cref="ReconcileFolders"/> but for the
    /// <c>template_module_folders</c> rows that scaffold module extensions.
    /// </summary>
    private static void ReconcileModuleFolders(RuntimeTemplate existing, IReadOnlyList<TemplateFolderInput> inputs, int orgId)
    {
        var existingFolders = existing.ModuleFolders.OrderBy(f => f.Ordering).ToList();

        for (var i = 0; i < inputs.Count; i++)
        {
            var inputFolder = inputs[i];
            var path = inputFolder.Path.Trim();

            TemplateModuleFolder folder;
            if (i < existingFolders.Count)
            {
                folder = existingFolders[i];
                folder.Ordering = i;
                folder.Path = path;
            }
            else
            {
                folder = new TemplateModuleFolder { OrganizationId = orgId, Ordering = i, Path = path };
                existing.ModuleFolders.Add(folder);
            }

            ReconcileModuleFiles(folder, inputFolder.Files, orgId);
        }

        for (var i = inputs.Count; i < existingFolders.Count; i++)
        {
            existing.ModuleFolders.Remove(existingFolders[i]);
        }
    }

    /// <summary>
    /// Same incremental update as <see cref="ReconcileFiles"/> but for the
    /// <c>template_module_files</c> rows hung off a module folder.
    /// </summary>
    private static void ReconcileModuleFiles(TemplateModuleFolder folder, IReadOnlyList<TemplateFileInput> inputs, int orgId)
    {
        var existingFiles = folder.Files.OrderBy(f => f.Ordering).ToList();

        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var path = input.Path.Trim();
            var content = input.Content ?? string.Empty;

            if (i < existingFiles.Count)
            {
                var file = existingFiles[i];
                file.Ordering = i;
                file.Path = path;
                file.Content = content;
                file.IsExample = input.IsExample;
            }
            else
            {
                folder.Files.Add(new TemplateModuleFile
                {
                    OrganizationId = orgId,
                    Ordering = i,
                    Path = path,
                    Content = content,
                    IsExample = input.IsExample,
                });
            }
        }

        for (var i = inputs.Count; i < existingFiles.Count; i++)
        {
            folder.Files.Remove(existingFiles[i]);
        }
    }

    /// <summary>
    /// Reconciles the template's <c>runtime_template_default_modules</c> rows
    /// against the validated module-id list. Like the folder/file reconciler,
    /// this mutates existing rows in-order so unchanged join rows keep stable
    /// primary keys and the audit log only captures the rows that really moved.
    /// </summary>
    private static void ReconcileDefaultModules(RuntimeTemplate existing, IReadOnlyList<int> moduleIds, int orgId)
    {
        var existingDefaults = existing.DefaultModules.OrderBy(d => d.Ordering).ToList();

        for (var i = 0; i < moduleIds.Count; i++)
        {
            var moduleId = moduleIds[i];
            if (i < existingDefaults.Count)
            {
                var row = existingDefaults[i];
                row.Ordering = i;
                row.ModuleId = moduleId;
                // Drop the old navigation reference so EF doesn't treat the
                // FK change as ambiguous when the previously-attached Module
                // is still tracked.
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

        for (var i = moduleIds.Count; i < existingDefaults.Count; i++)
        {
            existing.DefaultModules.Remove(existingDefaults[i]);
        }
    }

    /// <summary>
    /// Same incremental update as <see cref="ReconcileFolders"/> but for the
    /// <c>template_files</c> rows hung off a single folder.
    /// </summary>
    private static void ReconcileFiles(TemplateFolder folder, IReadOnlyList<TemplateFileInput> inputs, int orgId)
    {
        var existingFiles = folder.Files.OrderBy(f => f.Ordering).ToList();

        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var path = input.Path.Trim();
            var content = input.Content ?? string.Empty;

            if (i < existingFiles.Count)
            {
                var file = existingFiles[i];
                file.Ordering = i;
                file.Path = path;
                file.Content = content;
                file.IsExample = input.IsExample;
            }
            else
            {
                folder.Files.Add(new TemplateFile
                {
                    OrganizationId = orgId,
                    Ordering = i,
                    Path = path,
                    Content = content,
                    IsExample = input.IsExample,
                });
            }
        }

        for (var i = inputs.Count; i < existingFiles.Count; i++)
        {
            folder.Files.Remove(existingFiles[i]);
        }
    }

    /// <summary>
    /// Validates an admin input payload. Returns the parsed <see cref="TemplateDefaults"/>
    /// so the caller doesn't reparse it. Throws a <see cref="PlanValidationException"/>
    /// aggregating every error so the form can render all of them on a single
    /// round-trip.
    /// </summary>
    private async Task<(TemplateDefaults Defaults, IReadOnlyList<int> DefaultModuleIds, int? DefaultApplicationVersionId)> ValidateAsync(
        TemplateInput input,
        int? existingId,
        CancellationToken ct)
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
            var keyOwner = await _db.RuntimeTemplates
                .AsNoTracking()
                .Where(t => t.Key == key)
                .Select(t => (int?)t.Id)
                .FirstOrDefaultAsync(ct);
            if (keyOwner is not null && keyOwner != existingId)
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

        if (string.IsNullOrWhiteSpace(input.DefaultApplication))
        {
            errors[nameof(input.DefaultApplication)] = "Default application version is required.";
        }

        if (string.IsNullOrWhiteSpace(input.DefaultPlatform))
        {
            errors[nameof(input.DefaultPlatform)] = "Default platform version is required.";
        }

        if (input.CoreIdRangeFrom <= 0)
        {
            errors[nameof(input.CoreIdRangeFrom)] = "Core ID range start must be greater than zero.";
        }
        if (input.CoreIdRangeTo <= input.CoreIdRangeFrom)
        {
            errors[nameof(input.CoreIdRangeTo)] = "Core ID range end must be greater than the start.";
        }

        if (input.ModuleIdRangeStart <= 0)
        {
            errors[nameof(input.ModuleIdRangeStart)] = "Module ID range start must be greater than zero.";
        }
        if (input.ModuleIdRangeSize <= 0)
        {
            errors[nameof(input.ModuleIdRangeSize)] = "Module ID range size must be greater than zero.";
        }

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

        // .code-workspace content must be non-empty (the generator writes
        // exactly what the template declares, so an empty string would
        // produce a broken .code-workspace) and must carry the {{paths}}
        // placeholder so generation has somewhere to insert the workspace's
        // Core + module folder entries.
        if (string.IsNullOrWhiteSpace(input.WorkspaceTemplate))
        {
            errors[nameof(input.WorkspaceTemplate)] = ".code-workspace content is required.";
        }
        else if (!input.WorkspaceTemplate.Contains("{{paths}}", StringComparison.Ordinal))
        {
            errors[nameof(input.WorkspaceTemplate)] =
                ".code-workspace content must contain {{paths}} — the generator substitutes it with the workspace's folder list.";
        }

        // Case-insensitive uniqueness for folders and files: Windows treats
        // 'src/Foo' and 'src/foo' as the same path, so admitting both would
        // fail at extraction time. Mirror that here. Same rules apply to
        // module folders, just under their own field-key namespace.
        ValidateFolderInputs(input.Folders, "Folders", errors);
        ValidateFolderInputs(input.ModuleFolders, "ModuleFolders", errors);

        // Resolve default-module keys to ids. Duplicates in the input list are
        // collapsed (preserving the first occurrence's order); unknown or
        // soft-deleted module keys surface as a single field-keyed error so
        // the admin sees exactly which row to fix.
        var defaultModuleIds = new List<int>();
        if (input.DefaultModuleKeys.Count > 0)
        {
            var trimmedKeys = input.DefaultModuleKeys
                .Select(k => k?.Trim() ?? string.Empty)
                .Where(k => !string.IsNullOrEmpty(k))
                .ToList();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var orderedUnique = new List<string>();
            foreach (var k in trimmedKeys)
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
                errors[nameof(input.DefaultModuleKeys)] =
                    $"Unknown default module(s): {string.Join(", ", missing)}.";
            }
            else
            {
                defaultModuleIds = orderedUnique.Select(k => idByKey[k]).ToList();
            }
        }

        // Optional curated application-version key (Milestone P2.4). An empty
        // key means "no curated entry"; a present key must resolve to a live
        // (non-deleted) row. Soft-deleted rows are rejected so the form can't
        // accidentally re-link a template to a removed catalogue entry.
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
                errors[nameof(input.DefaultApplicationVersionKey)] =
                    $"Unknown application version '{versionKey}'.";
            }
            else
            {
                defaultApplicationVersionId = resolved;
            }
        }

        if (errors.Count > 0)
        {
            throw new PlanValidationException(errors);
        }

        return (defaults, defaultModuleIds, defaultApplicationVersionId);
    }
}

/// <summary>
/// Form-shaped admin input for create/update operations. The two JSON columns
/// are accepted as raw strings so the textarea-based editor in
/// <c>/admin/templates/{key}</c> can hand them through unmodified; the service
/// validates and parses them.
/// </summary>
public record TemplateInput(
    string Key,
    string Runtime,
    string Name,
    string? Description,
    string DefaultApplication,
    string DefaultPlatform,
    string DefaultsJson,
    int CoreIdRangeFrom,
    int CoreIdRangeTo,
    int ModuleIdRangeStart,
    int ModuleIdRangeSize,
    bool Deprecated,
    IReadOnlyList<string> DefaultModuleKeys,
    IReadOnlyList<TemplateFolderInput> Folders,
    IReadOnlyList<TemplateFolderInput> ModuleFolders,
    string WorkspaceTemplate,
    string? DefaultApplicationVersionKey = null);

/// <summary>One folder row submitted by the admin folder editor, with its files.</summary>
public record TemplateFolderInput(string Path, IReadOnlyList<TemplateFileInput> Files);

/// <summary>One file row submitted by the admin file editor.</summary>
public record TemplateFileInput(string Path, string Content, bool IsExample = false);
