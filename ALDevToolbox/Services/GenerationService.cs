using System.Diagnostics;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Generation;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Builds an in-memory ZIP archive for a workspace or standalone extension
/// under the unified-extensions model (Issue #54). The caller streams the
/// resulting bytes to the HTTP response — algorithm in
/// <c>.design/generation-engine.md</c> and the per-extension contract in
/// <c>.design/unified-extensions.md</c>.
/// </summary>
/// <remarks>
/// Orchestrator only since #86: validation + DB loads + extension list build
/// happen here, and the actual ZIP writes are delegated to
/// <see cref="WorkspaceZipBuilder"/>. Mustache substitution is delegated to
/// <see cref="MustacheRenderer"/>.
/// </remarks>
public class GenerationService
{
    private static readonly Regex WorkspaceNameRegex = new(@"^[A-Za-z][A-Za-z0-9 ]*$", RegexOptions.Compiled);
    private static readonly Regex ExtensionNameRegex = new(@"^[A-Za-z][A-Za-z0-9]*$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly OrganizationConfigService _orgConfig;
    private readonly FolderTreeHydrator _folderTree;
    private readonly IOrganizationContext _orgContext;
    private readonly MustacheRenderer _mustache;
    private readonly WorkspaceZipBuilder _zipBuilder;
    private readonly ILogger<GenerationService> _logger;

    public GenerationService(
        AppDbContext db,
        OrganizationConfigService orgConfig,
        FolderTreeHydrator folderTree,
        IOrganizationContext orgContext,
        MustacheRenderer mustache,
        WorkspaceZipBuilder zipBuilder,
        ILogger<GenerationService> logger)
    {
        _db = db;
        _orgConfig = orgConfig;
        _folderTree = folderTree;
        _orgContext = orgContext;
        _mustache = mustache;
        _zipBuilder = zipBuilder;
        _logger = logger;
    }

    private Task<OrganizationConfig> GetOrgConfigAsync(CancellationToken ct) =>
        _orgConfig.GetForAsync(
            _orgContext.CurrentOrganizationId
                ?? throw new InvalidOperationException("Generation invoked without an organisation in scope."),
            ct);

    // ===== Workspace flow =====

    /// <summary>
    /// Generates a workspace ZIP for the given plan. The walk concatenates the
    /// template's required extensions, any optional extensions the user
    /// ticked, and one cloned extension per selected catalogue module — all
    /// in their display order.
    /// </summary>
    public async Task<GeneratedArchive> GenerateWorkspaceAsync(ProjectPlan plan, CancellationToken ct = default)
    {
        ValidateWorkspacePlan(plan);

        var stopwatch = Stopwatch.StartNew();
        var template = await LoadTemplateAsync(plan.TemplateKey, ct);
        var modules = await LoadSelectedModulesAsync(plan.SelectedModuleKeys, ct);
        var orgConfig = await GetOrgConfigAsync(ct);

        // {{publisher}} resolves to the org's configuration default, falling
        // back to the template default for a fresh org. Resolved once here and
        // threaded into every extension so the per-extension app.json and the
        // workspace-root files agree. See GenerationNaming.ResolvePublisher.
        var publisher = GenerationNaming.ResolvePublisher(
            orgConfig.Settings.DefaultPublisher, template.Defaults.Publisher);

        var extensions = BuildExtensionList(template, plan, modules, publisher);
        ValidateIdRanges(extensions);

        var (stream, fileCount) = await _zipBuilder.BuildWorkspaceAsync(plan, template, extensions, orgConfig, ct);
        var shortName = GenerationNaming.StripWhitespace(plan.WorkspaceName);
        stopwatch.Stop();

        _logger.LogInformation(
            "Generated workspace '{Workspace}' from template '{Template}' with [{Extensions}]: {Files} files, {Bytes} bytes, {Ms} ms.",
            plan.WorkspaceName,
            plan.TemplateKey,
            string.Join(",", extensions.Select(e => e.Path)),
            fileCount,
            stream.Length,
            stopwatch.ElapsedMilliseconds);

        return new GeneratedArchive(stream, $"{shortName}.zip");
    }

    // ===== Standalone extension flow =====

    /// <summary>
    /// Generates a single-extension ZIP for the New Extension flow. The
    /// emitted layout reuses the workspace template's first extension as the
    /// scaffold (typically Core); dependencies come from the form rather than
    /// the template's declarations.
    /// </summary>
    public async Task<GeneratedArchive> GenerateExtensionAsync(
        StandaloneExtensionPlan plan,
        SiblingWorkspaceContext? sibling = null,
        CancellationToken ct = default)
    {
        ValidateExtensionPlan(plan);

        var stopwatch = Stopwatch.StartNew();
        var template = await LoadTemplateAsync(plan.TemplateKey, ct);

        // Use the first (required) template extension as the scaffold for a
        // standalone build. Falls back to no template folders when the
        // template doesn't declare any extensions — the static fallback
        // folders below carry the structure.
        var scaffold = template.WorkspaceExtensions
            .OrderBy(e => e.Ordering)
            .FirstOrDefault(e => e.Required);
        var folderRoots = scaffold is null
            ? new List<FolderNode>()
            : BuildFolderTree(scaffold.Folders);

        // OrgConfig is needed both for the sibling-rewrite path (which
        // needs the org's workspace JSON template) and for the per-extension
        // org files the standalone extension might opt into via
        // RuntimeTemplateIncludedFile. Always load it now.
        var orgConfig = await GetOrgConfigAsync(ct);

        var (stream, fileCount, folderName) = await _zipBuilder.BuildStandaloneAsync(
            plan, template, folderRoots, sibling, orgConfig, ct);
        stopwatch.Stop();

        _logger.LogInformation(
            "Generated {Mode} extension '{Extension}' from template '{Template}' with {Deps} deps: {Files} files, {Bytes} bytes, {Ms} ms.",
            sibling is null ? "standalone" : "sibling",
            plan.ExtensionName,
            plan.TemplateKey,
            plan.Dependencies.Count,
            fileCount,
            stream.Length,
            stopwatch.ElapsedMilliseconds);

        return new GeneratedArchive(stream, $"{folderName}.zip");
    }

    // ===== Loading =====

    private async Task<RuntimeTemplate> LoadTemplateAsync(string key, CancellationToken ct)
    {
        // EF doesn't support a recursive Include on the folder tree, so the
        // top-level Folders + Files come through here and any nested levels
        // are loaded as a flat list below and reassembled.
        var template = await _db.RuntimeTemplates
            .AsNoTracking()
            .Where(t => t.DeletedAt == null && t.Key == key)
            .Include(t => t.WorkspaceExtensions.OrderBy(e => e.Ordering))
                .ThenInclude(e => e.Dependencies.OrderBy(d => d.Ordering))
            .Include(t => t.IncludedFiles.OrderBy(j => j.Ordering))
            .FirstOrDefaultAsync(ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["TemplateKey"] = $"Template '{key}' was not found.",
            });

        // Folder tree hydration is delegated to FolderTreeHydrator so workspace
        // generation, template authoring loads, and cross-org imports share
        // one implementation (#77).
        await _folderTree.HydrateExtensionFolderTreeAsync(new[] { template }, ct);
        return template;
    }

    private async Task<List<Module>> LoadSelectedModulesAsync(IReadOnlyList<string> moduleKeys, CancellationToken ct)
    {
        if (moduleKeys.Count == 0) return new();

        var modules = await _db.Modules
            .AsNoTracking()
            .Where(m => m.DeletedAt == null && moduleKeys.Contains(m.Key))
            .Include(m => m.Dependencies.OrderBy(d => d.Ordering))
            .ToListAsync(ct);

        await _folderTree.HydrateModuleExtensionFolderTreeAsync(modules, ct);

        // Preserve user-selected ordering — EF returns them in whatever
        // order the IN-clause matched.
        var byKey = modules.ToDictionary(m => m.Key);
        var ordered = new List<Module>(moduleKeys.Count);
        foreach (var key in moduleKeys)
        {
            if (byKey.TryGetValue(key, out var module)) ordered.Add(module);
        }
        return ordered;
    }

    // ===== Extension list building =====

    /// <summary>
    /// Walks the template's declared extensions in display order — required
    /// first, then the optional ones the user ticked — and appends one cloned
    /// extension per selected catalogue module. Each
    /// <see cref="EmittableExtension"/> carries a fresh GUID, its resolved
    /// id-range, the substituted display name, and the source folder tree.
    /// </summary>
    private List<EmittableExtension> BuildExtensionList(RuntimeTemplate template, ProjectPlan plan, IReadOnlyList<Module> modules, string publisher)
    {
        var selectedOptional = new HashSet<string>(plan.SelectedExtensionPaths, StringComparer.Ordinal);
        var list = new List<EmittableExtension>();

        // ID-range cursor: starts at the template's first auto-allocate slot
        // (ModuleIdRangeStart) and walks forward. The first extension consumes
        // the Core range from the plan when it has no explicit ids; subsequent
        // unannotated extensions take a slice from the cursor.
        var cursor = template.ModuleIdRangeStart;
        var firstAuto = true;

        foreach (var ext in template.WorkspaceExtensions.OrderBy(e => e.Ordering))
        {
            if (!ext.Required && !selectedOptional.Contains(ext.Path)) continue;
            var (from, to, advancedCursor) = ResolveTemplateRange(ext, template, plan, firstAuto, cursor);
            cursor = advancedCursor;
            if (ext.IdRangeFrom is null && ext.IdRangeTo is null) firstAuto = false;

            list.Add(BuildFromTemplate(ext, template, plan, from, to, publisher));
        }

        foreach (var module in modules)
        {
            var size = module.IdRangeSize ?? template.ModuleIdRangeSize;
            var from = cursor;
            var to = from + size - 1;
            cursor = to + 1;
            list.Add(BuildFromModule(module, template, plan, from, to, publisher));
        }

        return list;
    }

    private static (int From, int To, int Cursor) ResolveTemplateRange(
        WorkspaceExtension ext, RuntimeTemplate template, ProjectPlan plan, bool firstAuto, int cursor)
    {
        // Explicit on the extension: use verbatim, don't move the cursor.
        if (ext.IdRangeFrom is int explicitFrom && ext.IdRangeTo is int explicitTo)
        {
            return (explicitFrom, explicitTo, cursor);
        }
        // First unannotated template extension: take the plan's Core range.
        if (firstAuto)
        {
            return (plan.CoreIdRangeFrom, plan.CoreIdRangeTo, cursor);
        }
        // Subsequent unannotated extensions: slice the template's module range.
        var size = template.ModuleIdRangeSize;
        return (cursor, cursor + size - 1, cursor + size);
    }

    private EmittableExtension BuildFromTemplate(WorkspaceExtension ext, RuntimeTemplate template, ProjectPlan plan, int from, int to, string publisher)
    {
        var name = SubstituteScalar(ext.NameTemplate, plan, template);
        return new EmittableExtension(
            Path: ext.Path,
            Name: name,
            Id: Guid.NewGuid(),
            IdRangeFrom: from,
            IdRangeTo: to,
            Application: !string.IsNullOrEmpty(ext.Application) ? ext.Application : plan.ApplicationVersion,
            Runtime: !string.IsNullOrEmpty(ext.Runtime) ? ext.Runtime : plan.RuntimeVersion,
            Publisher: publisher,
            IsModuleClone: false,
            ModuleKey: null,
            ModuleName: name,
            FolderRoots: BuildFolderTree(ext.Folders),
            Dependencies: ext.Dependencies
                .OrderBy(d => d.Ordering)
                .Select(d => new EmittableDependency(d.RefExtensionPath, d.RefModuleKey, d.LitId, d.LitName, d.LitPublisher, d.LitVersion))
                .ToList());
    }

    private EmittableExtension BuildFromModule(Module module, RuntimeTemplate template, ProjectPlan plan, int from, int to, string publisher)
    {
        // The cloned extension's folder name and rendered AL name both come
        // from Module.ExtensionName (a PascalCase admin-controlled value).
        // Module.Key stays as the URL/admin slug and the dep ref target —
        // not the folder.
        var nameTemplate = $"{{{{extension_prefix}}}} {module.ExtensionName}";
        var name = SubstituteScalar(nameTemplate, plan, template);

        // Module dependencies (from module_dependencies) become literal deps.
        // Implicit dependencies on every required template-declared extension
        // get added in WorkspaceZipBuilder.ResolveDependencies at emit time
        // so they pick up the freshly-generated GUIDs from the rest of the
        // list (used when rendering {{dependencies_array}} in app.json).
        var deps = module.Dependencies
            .OrderBy(d => d.Ordering)
            .Select(d => new EmittableDependency(null, null, d.DepId, d.DepName, d.DepPublisher, d.DepVersion))
            .ToList();

        return new EmittableExtension(
            Path: module.ExtensionName,
            Name: name,
            Id: Guid.NewGuid(),
            IdRangeFrom: from,
            IdRangeTo: to,
            Application: plan.ApplicationVersion,
            Runtime: plan.RuntimeVersion,
            Publisher: publisher,
            IsModuleClone: true,
            ModuleKey: module.Key,
            ModuleName: module.ExtensionName,
            FolderRoots: BuildModuleFolderTree(module.ExtensionFolders),
            Dependencies: deps);
    }

    private static List<FolderNode> BuildFolderTree(IEnumerable<WorkspaceExtensionFolder> roots) =>
        roots.OrderBy(f => f.Ordering)
            .Select(f => new FolderNode(
                f.Path,
                f.Files.OrderBy(x => x.Ordering).Select(x => new FileLeaf(x.Path, x.Content, x.IsExample)).ToList(),
                BuildFolderTree(f.Folders)))
            .ToList();

    private static List<FolderNode> BuildModuleFolderTree(IEnumerable<ModuleExtensionFolder> roots) =>
        roots.OrderBy(f => f.Ordering)
            .Select(f => new FolderNode(
                f.Path,
                f.Files.OrderBy(x => x.Ordering).Select(x => new FileLeaf(x.Path, x.Content, x.IsExample)).ToList(),
                BuildModuleFolderTree(f.Folders)))
            .ToList();

    // ===== Validation =====

    private static void ValidateWorkspacePlan(ProjectPlan plan)
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(plan.TemplateKey)) errors[nameof(plan.TemplateKey)] = "Required.";
        if (string.IsNullOrWhiteSpace(plan.WorkspaceName) || !WorkspaceNameRegex.IsMatch(plan.WorkspaceName))
            errors[nameof(plan.WorkspaceName)] = "Required. Letters, digits and spaces only; must start with a letter.";
        if (plan.CoreIdRangeFrom <= 0) errors[nameof(plan.CoreIdRangeFrom)] = "Must be greater than zero.";
        if (plan.CoreIdRangeTo <= plan.CoreIdRangeFrom) errors[nameof(plan.CoreIdRangeTo)] = "Must be greater than 'from'.";
        if (string.IsNullOrWhiteSpace(plan.ApplicationVersion)) errors[nameof(plan.ApplicationVersion)] = "Required.";
        if (string.IsNullOrWhiteSpace(plan.RuntimeVersion)) errors[nameof(plan.RuntimeVersion)] = "Required.";
        if (errors.Count > 0) throw new PlanValidationException(errors);
    }

    private static void ValidateExtensionPlan(StandaloneExtensionPlan plan)
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(plan.TemplateKey)) errors[nameof(plan.TemplateKey)] = "Required.";
        if (string.IsNullOrWhiteSpace(plan.ExtensionName) || !ExtensionNameRegex.IsMatch(plan.ExtensionName))
            errors[nameof(plan.ExtensionName)] = "Required. Letters and digits only, no spaces.";
        if (string.IsNullOrWhiteSpace(plan.Publisher)) errors[nameof(plan.Publisher)] = "Required.";
        if (plan.IdRangeFrom <= 0) errors[nameof(plan.IdRangeFrom)] = "Must be greater than zero.";
        if (plan.IdRangeTo <= plan.IdRangeFrom) errors[nameof(plan.IdRangeTo)] = "Must be greater than 'from'.";
        if (string.IsNullOrWhiteSpace(plan.ApplicationVersion)) errors[nameof(plan.ApplicationVersion)] = "Required.";
        if (string.IsNullOrWhiteSpace(plan.RuntimeVersion)) errors[nameof(plan.RuntimeVersion)] = "Required.";

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < plan.Dependencies.Count; i++)
        {
            var dep = plan.Dependencies[i];
            if (string.IsNullOrWhiteSpace(dep.DepId)) continue;
            if (!seenIds.Add(dep.DepId.Trim()))
            {
                errors[$"Dependencies[{i}].DepId"] = $"Duplicate dependency id '{dep.DepId}'.";
            }
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);
    }

    /// <summary>
    /// Walks the final extension list and rejects the plan when any two
    /// resolved id ranges overlap. Per <c>unified-extensions.md</c> a
    /// workspace's allocated ranges have to be disjoint or AL refuses to
    /// compile a multi-extension app.
    /// </summary>
    private static void ValidateIdRanges(IReadOnlyList<EmittableExtension> extensions)
    {
        var errors = new Dictionary<string, string>();
        for (var i = 0; i < extensions.Count; i++)
        {
            var a = extensions[i];
            for (var j = i + 1; j < extensions.Count; j++)
            {
                var b = extensions[j];
                if (a.IdRangeFrom <= b.IdRangeTo && b.IdRangeFrom <= a.IdRangeTo)
                {
                    errors[$"Extensions[{j}].IdRange"] =
                        $"Extension '{b.Path}' id range {b.IdRangeFrom}..{b.IdRangeTo} overlaps '{a.Path}' {a.IdRangeFrom}..{a.IdRangeTo}.";
                }
            }
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);
    }

    // ===== Mustache substitution =====

    /// <summary>
    /// Scalar substitution used for the extension name (no folder-context
    /// awareness — names are built before folder traversal). Builds an
    /// empty-FolderPath context and delegates to <see cref="MustacheRenderer.Render"/>.
    /// </summary>
    private string SubstituteScalar(string source, ProjectPlan plan, RuntimeTemplate template)
    {
        var ctx = new MustacheContext(
            Name: source,
            WorkspaceName: plan.WorkspaceName,
            ShortName: GenerationNaming.StripWhitespace(plan.WorkspaceName),
            ModuleName: source,
            Publisher: template.Defaults.Publisher,
            ExtensionPrefix: plan.ExtensionPrefix,
            Affix: template.Defaults.AffixType == AffixType.None ? string.Empty : template.Defaults.Affix,
            FolderPath: string.Empty,
            TenantId: plan.TenantId);
        return _mustache.Render(source, ctx);
    }
}

/// <summary>Container for a finished archive. The stream is rewound and ready to copy to the HTTP response body.</summary>
public record GeneratedArchive(MemoryStream Stream, string FileName);

/// <summary>Sibling-extension context for the New Extension flow.</summary>
public record SiblingWorkspaceContext(
    string WorkspaceName,
    IReadOnlyList<string> ModuleKeys,
    IReadOnlyList<string> ExistingFolders);
