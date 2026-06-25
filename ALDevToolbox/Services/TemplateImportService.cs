using System.Diagnostics;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Cross-org "fork" pipeline: copies a template (plus its referenced
/// modules and application version) from the singleton system org
/// (<see cref="Organization.IsSystem"/>) into the acting user's organisation.
/// The system org is the canonical catalogue — admins author there via the
/// regular <c>/admin/templates</c> pages; everyone else imports on demand.
///
/// Imports are one-way clones, not subscriptions. Once an org has imported a
/// template, the local copy is independent — later edits in the system org
/// don't propagate, and the importing admin can rename/deprecate/delete
/// without affecting the system.
/// </summary>
public sealed class TemplateImportService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly FolderTreeHydrator _folderTree;
    private readonly StorageQuotaGuard _quotaGuard;
    private readonly ILogger<TemplateImportService> _logger;

    public TemplateImportService(
        AppDbContext db,
        IOrganizationContext orgContext,
        FolderTreeHydrator folderTree,
        StorageQuotaGuard quotaGuard,
        ILogger<TemplateImportService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _folderTree = folderTree;
        _quotaGuard = quotaGuard;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; import called outside an authenticated request.");

    /// <summary>
    /// Returns the system org's active templates with a flag indicating whether
    /// the acting user's org already has a template with the same key (so the
    /// UI can show "Already imported" instead of an Import button). Cross-org
    /// reads bypass the EF query filter.
    /// </summary>
    public async Task<List<SystemTemplateSummary>> ListSystemTemplatesAsync(CancellationToken ct = default)
    {
        var systemOrgId = await _db.Organizations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(o => o.IsSystem)
            .Select(o => (int?)o.Id)
            .FirstOrDefaultAsync(ct);
        if (systemOrgId is null) return new List<SystemTemplateSummary>();

        var actingOrgId = RequireOrganizationId();

        var systemTemplates = await _db.RuntimeTemplates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.OrganizationId == systemOrgId.Value && t.DeletedAt == null && !t.Deprecated)
            .Select(t => new
            {
                t.Id,
                t.Key,
                t.Name,
                t.Description,
                t.Runtime,
            })
            .ToListAsync(ct);
        if (systemTemplates.Count == 0) return new List<SystemTemplateSummary>();

        var keys = systemTemplates.Select(t => t.Key).ToList();
        var localKeys = await _db.RuntimeTemplates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.OrganizationId == actingOrgId && keys.Contains(t.Key))
            .Select(t => t.Key)
            .ToListAsync(ct);
        var localKeySet = new HashSet<string>(localKeys, StringComparer.Ordinal);

        return systemTemplates
            .Select(t => new SystemTemplateSummary(
                Id: t.Id,
                Key: t.Key,
                Name: t.Name,
                Description: t.Description,
                Runtime: t.Runtime,
                AlreadyImported: localKeySet.Contains(t.Key)))
            .OrderBy(t => TemplateService.RuntimeSortKey(t.Runtime))
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Copies a system-org template into the acting user's organisation. The
    /// referenced default modules and the default application version are
    /// imported alongside (deduplicated by key against the local catalogue).
    /// All writes commit in a single transaction.
    /// </summary>
    /// <exception cref="PlanValidationException">
    /// Refuses when the acting org is the system org itself, when no template
    /// matches the supplied id, or when the local org already has a template
    /// with the same key. Field-keyed messages so the UI can surface them inline.
    /// </exception>
    public async Task<RuntimeTemplate> ImportTemplateAsync(int systemTemplateId, CancellationToken ct = default)
    {
        var actingOrgId = RequireOrganizationId();
        if (_orgContext.IsSystemOrganization)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Import"] = "The system organisation can't import from itself.",
            });
        }

        await _quotaGuard.EnsureCanWriteAsync(ct);

        var systemOrgId = await _db.Organizations
            .IgnoreQueryFilters()
            .Where(o => o.IsSystem)
            .Select(o => (int?)o.Id)
            .FirstOrDefaultAsync(ct);
        if (systemOrgId is null)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Import"] = "No system organisation is configured; ask a SiteAdmin to publish a template first.",
            });
        }

        // AsNoTracking: the source is read-only — we fork it into the acting
        // org. Hydrating the folder tree mutates the source's nav collections
        // in memory; with tracking on, EF would try to persist those edits
        // (including the untracked folder rows shoved into them) against the
        // source. CLAUDE.md: AsNoTracking on every read-only EF query.
        var source = await _db.RuntimeTemplates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.Id == systemTemplateId && t.OrganizationId == systemOrgId.Value)
            .Include(t => t.WorkspaceExtensions.OrderBy(e => e.Ordering))
                .ThenInclude(e => e.Dependencies.OrderBy(d => d.Ordering))
            .Include(t => t.DefaultModules.OrderBy(d => d.Ordering))
                .ThenInclude(d => d.Module!)
                    .ThenInclude(m => m.Dependencies.OrderBy(dep => dep.Ordering))
            .Include(t => t.DefaultApplicationVersion)
            .FirstOrDefaultAsync(ct);
        if (source is null)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Import"] = "That template no longer exists in the system catalogue.",
            });
        }

        var keyClash = await _db.RuntimeTemplates
            .IgnoreQueryFilters()
            .AnyAsync(t => t.OrganizationId == actingOrgId && t.Key == source.Key, ct);
        if (keyClash)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Key"] = $"This organisation already has a template with key '{source.Key}'.",
            });
        }

        var stopwatch = Stopwatch.StartNew();
        var now = DateTime.UtcNow;

        var localVersion = source.DefaultApplicationVersion is null
            ? null
            : await EnsureApplicationVersionAsync(source.DefaultApplicationVersion, actingOrgId, now, ct);

        // Source extensions only carry their dependencies via Include. The
        // recursive folder/file tree is loaded flat below and reassembled —
        // EF doesn't recurse on Include and AsNoTracking doesn't do fixup.
        // Delegate to FolderTreeHydrator so workspace generation, template
        // authoring, and cross-org imports share one implementation (#77).
        // ignoreOrgFilter: the source is the system org, not the acting one.
        await _folderTree.HydrateExtensionFolderTreeAsync(new[] { source }, ct, ignoreOrgFilter: true);

        var localModulesByKey = new Dictionary<string, Module>(StringComparer.Ordinal);
        var sourceModules = source.DefaultModules
            .Select(dm => dm.Module ?? throw new InvalidOperationException("Default-module row has no module loaded."))
            .GroupBy(m => m.Key)
            .Select(g => g.First())
            .ToList();
        await _folderTree.HydrateModuleExtensionFolderTreeAsync(sourceModules, ct, ignoreOrgFilter: true);
        foreach (var sourceModule in sourceModules)
        {
            localModulesByKey[sourceModule.Key] = await EnsureModuleAsync(sourceModule, actingOrgId, now, ct);
        }

        var clone = new RuntimeTemplate
        {
            OrganizationId = actingOrgId,
            Key = source.Key,
            Runtime = source.Runtime,
            Name = source.Name,
            Description = source.Description,
            DefaultApplicationVersion = localVersion,
            Defaults = source.Defaults,
            AppSourceCop = source.AppSourceCop,
            CoreIdRangeFrom = source.CoreIdRangeFrom,
            CoreIdRangeTo = source.CoreIdRangeTo,
            ModuleIdRangeStart = source.ModuleIdRangeStart,
            ModuleIdRangeSize = source.ModuleIdRangeSize,
            Deprecated = false,
            CreatedAt = now,
            UpdatedAt = now,
            WorkspaceExtensions = source.WorkspaceExtensions
                .OrderBy(e => e.Ordering)
                .Select(e => CloneWorkspaceExtension(e, actingOrgId))
                .ToList(),
        };

        var ordering = 0;
        foreach (var defaultModule in source.DefaultModules)
        {
            var key = defaultModule.Module!.Key;
            if (!localModulesByKey.TryGetValue(key, out var localModule)) continue;
            clone.DefaultModules.Add(new RuntimeTemplateDefaultModule
            {
                OrganizationId = actingOrgId,
                Module = localModule,
                Ordering = ordering++,
            });
        }

        _db.RuntimeTemplates.Add(clone);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Imported template '{Key}' from system org {SystemOrgId} into org {OrgId}: {Extensions} extension(s), {Modules} module(s), elapsed {Elapsed}ms.",
            clone.Key, systemOrgId.Value, actingOrgId,
            clone.WorkspaceExtensions.Count,
            clone.DefaultModules.Count,
            stopwatch.ElapsedMilliseconds);

        return clone;
    }

    /// <summary>Deep-clones a workspace extension into a fresh entity graph owned by the importing org.</summary>
    private static WorkspaceExtension CloneWorkspaceExtension(WorkspaceExtension source, int actingOrgId) => new()
    {
        OrganizationId = actingOrgId,
        Ordering = source.Ordering,
        Path = source.Path,
        NameTemplate = source.NameTemplate,
        Required = source.Required,
        Application = source.Application,
        Runtime = source.Runtime,
        IdRangeFrom = source.IdRangeFrom,
        IdRangeTo = source.IdRangeTo,
        Folders = source.Folders
            .OrderBy(f => f.Ordering)
            .Select(f => CloneWorkspaceFolder(f, actingOrgId))
            .ToList(),
        Dependencies = source.Dependencies
            .OrderBy(d => d.Ordering)
            .Select(d => new WorkspaceExtensionDependency
            {
                OrganizationId = actingOrgId,
                Ordering = d.Ordering,
                RefExtensionPath = d.RefExtensionPath,
                RefModuleKey = d.RefModuleKey,
                LitId = d.LitId,
                LitName = d.LitName,
                LitPublisher = d.LitPublisher,
                LitVersion = d.LitVersion,
            })
            .ToList(),
    };

    private static WorkspaceExtensionFolder CloneWorkspaceFolder(WorkspaceExtensionFolder source, int actingOrgId) => new()
    {
        OrganizationId = actingOrgId,
        Ordering = source.Ordering,
        Path = SafeSegment(source.Path, "folder"),
        Folders = source.Folders
            .OrderBy(f => f.Ordering)
            .Select(f => CloneWorkspaceFolder(f, actingOrgId))
            .ToList(),
        Files = source.Files
            .OrderBy(f => f.Ordering)
            .Select(f => new WorkspaceExtensionFile
            {
                OrganizationId = actingOrgId,
                Ordering = f.Ordering,
                Path = SafeSegment(f.Path, "file"),
                Content = f.Content,
                IsExample = f.IsExample,
            })
            .ToList(),
    };

    private static ModuleExtensionFolder CloneModuleExtensionFolder(ModuleExtensionFolder source, int actingOrgId) => new()
    {
        OrganizationId = actingOrgId,
        Ordering = source.Ordering,
        Path = SafeSegment(source.Path, "folder"),
        Folders = source.Folders
            .OrderBy(f => f.Ordering)
            .Select(f => CloneModuleExtensionFolder(f, actingOrgId))
            .ToList(),
        Files = source.Files
            .OrderBy(f => f.Ordering)
            .Select(f => new ModuleExtensionFile
            {
                OrganizationId = actingOrgId,
                Ordering = f.Ordering,
                Path = SafeSegment(f.Path, "file"),
                Content = f.Content,
                IsExample = f.IsExample,
            })
            .ToList(),
    };

    /// <summary>
    /// Import is the one place data crosses an org boundary. Folder/file paths
    /// from the system org are copied verbatim into the acting org and reach
    /// EmitFolderTree's ZIP entry paths, so assert each is a single safe segment
    /// (no separators, no <c>..</c>) at clone time — defence against a future
    /// authoring path or hand-seeded system org introducing a traversal segment
    /// that every importing org would silently inherit. See issue #389.
    /// </summary>
    private static string SafeSegment(string? path, string what)
    {
        var p = path ?? string.Empty;
        if (p.Length == 0
            || p.Contains("..", StringComparison.Ordinal)
            || p.Contains('/') || p.Contains('\\'))
        {
            throw new InvalidOperationException(
                $"Refusing to import a template {what} with an unsafe path segment '{p}'.");
        }
        return p;
    }

    private async Task<ApplicationVersion> EnsureApplicationVersionAsync(
        ApplicationVersion sourceVersion, int actingOrgId, DateTime now, CancellationToken ct)
    {
        var existing = await _db.ApplicationVersions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(v => v.OrganizationId == actingOrgId && v.Key == sourceVersion.Key, ct);
        if (existing is not null) return existing;

        var maxOrdering = await _db.ApplicationVersions
            .IgnoreQueryFilters()
            .Where(v => v.OrganizationId == actingOrgId)
            .Select(v => (int?)v.Ordering)
            .MaxAsync(ct) ?? -1;

        var clone = new ApplicationVersion
        {
            OrganizationId = actingOrgId,
            Key = sourceVersion.Key,
            Name = sourceVersion.Name,
            Application = sourceVersion.Application,
            Runtime = sourceVersion.Runtime,
            Ordering = maxOrdering + 1,
            Deprecated = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.ApplicationVersions.Add(clone);
        return clone;
    }

    private async Task<Module> EnsureModuleAsync(
        Module sourceModule, int actingOrgId, DateTime now, CancellationToken ct)
    {
        var existing = await _db.Modules
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.OrganizationId == actingOrgId && m.Key == sourceModule.Key, ct);
        if (existing is not null) return existing;

        var clone = new Module
        {
            OrganizationId = actingOrgId,
            Key = sourceModule.Key,
            Name = sourceModule.Name,
            ExtensionName = sourceModule.ExtensionName,
            IdRangeSize = sourceModule.IdRangeSize,
            Deprecated = false,
            CreatedAt = now,
            UpdatedAt = now,
            Dependencies = sourceModule.Dependencies.Select((d, i) => new ModuleDependency
            {
                OrganizationId = actingOrgId,
                Ordering = i,
                DepId = d.DepId,
                DepName = d.DepName,
                DepPublisher = d.DepPublisher,
                DepVersion = d.DepVersion,
            }).ToList(),
            // Module-supplied extension folder/file tree. Caller must have
            // hydrated the source module's recursive tree before invoking us.
            ExtensionFolders = sourceModule.ExtensionFolders
                .OrderBy(f => f.Ordering)
                .Select(f => CloneModuleExtensionFolder(f, actingOrgId))
                .ToList(),
        };
        _db.Modules.Add(clone);
        return clone;
    }
}

/// <summary>
/// Lightweight projection for the "From the site catalogue" list. Carries
/// just enough data to render the row and decide whether to show the Import
/// button or a disabled "Already imported" badge.
/// </summary>
public record SystemTemplateSummary(
    int Id,
    string Key,
    string Name,
    string? Description,
    string Runtime,
    bool AlreadyImported);
