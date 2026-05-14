using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Builds an in-memory ZIP archive for a workspace or standalone extension
/// under the unified-extensions model (Issue #54). The caller streams the
/// resulting bytes to the HTTP response — algorithm in
/// <c>.design/generation-engine.md</c> and the per-extension contract in
/// <c>.design/unified-extensions.md</c>.
/// </summary>
public class GenerationService
{
    private static readonly Regex MustacheRegex = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);
    private static readonly Regex WorkspaceNameRegex = new(@"^[A-Za-z][A-Za-z0-9 ]*$", RegexOptions.Compiled);
    private static readonly Regex ExtensionNameRegex = new(@"^[A-Za-z][A-Za-z0-9]*$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    private readonly AppDbContext _db;
    private readonly WorkspaceConfigService _config;
    private readonly OrganizationConfigService _orgConfig;
    private readonly TemplateService _templates;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<GenerationService> _logger;

    public GenerationService(
        AppDbContext db,
        WorkspaceConfigService config,
        OrganizationConfigService orgConfig,
        TemplateService templates,
        IOrganizationContext orgContext,
        ILogger<GenerationService> logger)
    {
        _db = db;
        _config = config;
        _orgConfig = orgConfig;
        _templates = templates;
        _orgContext = orgContext;
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

        var extensions = BuildExtensionList(template, plan, modules);
        ValidateIdRanges(extensions);

        var shortName = StripWhitespace(plan.WorkspaceName);
        var rootFolder = shortName;
        var stream = new MemoryStream();
        var fileCount = 0;

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Workspace-level assets: org logo, ruleset, always-included files.
            fileCount += WriteOrgLogo(archive, $"{rootFolder}/.assets/images", orgConfig.Logo);
            await WriteEmbeddedAsync(archive, $"{rootFolder}/.assets/rulesets/Company.ruleset.json", "ALDevToolbox.Resources.Company.ruleset.json", ct);
            fileCount += 1;
            fileCount += WriteOrgFiles(archive, rootFolder, orgConfig.Files, plan, template);

            // Per-extension folders.
            foreach (var ext in extensions)
            {
                fileCount += WriteExtension(archive, rootFolder, ext, extensions, template, plan);
            }

            // Workspace-root metadata.
            await WriteEmbeddedAsync(archive, $"{rootFolder}/.gitignore", "ALDevToolbox.Resources.al.gitignore", ct);
            var folderNames = extensions.Select(e => e.Path).ToList();
            var workspaceJsonCtx = new MustacheContext(
                Name: plan.WorkspaceName,
                WorkspaceName: plan.WorkspaceName,
                ShortName: shortName,
                ModuleName: plan.WorkspaceName,
                // Sourced from the template defaults to match WriteOrgFiles
                // (the always-included files use the same substitution table)
                // and to handle fresh orgs whose settings row is still blank.
                Publisher: template.Defaults.Publisher,
                ExtensionPrefix: plan.ExtensionPrefix,
                Affix: template.Defaults.AffixType == AffixType.None ? string.Empty : template.Defaults.Affix,
                FolderPath: string.Empty);
            WriteString(archive, $"{rootFolder}/{shortName}.code-workspace",
                BuildCodeWorkspace(
                    orgConfig.Settings.CodeWorkspaceJson,
                    template.CodeWorkspaceJson,
                    folderNames,
                    workspaceJsonCtx));
            WriteString(archive, $"{rootFolder}/README.md", BuildReadme(plan));
            var identities = extensions.Select(e => new WorkspaceExtensionIdentity(
                Kind: e.IsModuleClone ? WorkspaceExtensionIdentity.ModuleKind : WorkspaceExtensionIdentity.CoreKind,
                Key: e.ModuleKey,
                Id: e.Id,
                Name: e.Name,
                Folder: e.Path,
                Publisher: e.Publisher,
                IdRangeFrom: e.IdRangeFrom,
                IdRangeTo: e.IdRangeTo)).ToList();
            WriteString(archive, $"{rootFolder}/{WorkspaceConfigService.FileName}", _config.BuildWorkspace(plan, identities));
            fileCount += 4;
        }

        stream.Position = 0;
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

        var folderName = StripWhitespace(plan.ExtensionName);
        var stream = new MemoryStream();
        var fileCount = 0;

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var appJson = BuildStandaloneAppJson(plan, template);
            WriteString(archive, $"{folderName}/app.json", appJson);
            fileCount++;

            if (template.AppSourceCop.Include)
            {
                WriteString(archive, $"{folderName}/AppSourceCop.json", BuildAppSourceCopJson(template.AppSourceCop));
                fileCount++;
            }

            var substitutionCtx = new MustacheContext(
                Name: plan.ExtensionName,
                WorkspaceName: plan.ExtensionName,
                ShortName: StripWhitespace(plan.ExtensionName),
                ModuleName: plan.ExtensionName,
                Publisher: plan.Publisher,
                ExtensionPrefix: string.Empty,
                Affix: template.Defaults.AffixType == AffixType.None ? string.Empty : template.Defaults.Affix,
                FolderPath: string.Empty);
            fileCount += EmitFolderTree(archive, folderName, folderRoots, plan.IncludeExamples, substitutionCtx);

            WriteString(archive, $"{folderName}/{WorkspaceConfigService.FileName}", _config.BuildExtension(plan));
            fileCount++;

            if (sibling is not null)
            {
                var workspaceFile = $"{StripWhitespace(sibling.WorkspaceName)}.code-workspace";
                var existing = sibling.ExistingFolders.Count > 0
                    ? sibling.ExistingFolders.ToList()
                    : new List<string>();
                existing.Add(folderName);

                // Rewriting the sibling workspace's .code-workspace file: pull
                // the admin's JSON template from the org config so the result
                // matches what the workspace was originally generated with.
                var orgConfig = await GetOrgConfigAsync(ct);
                var siblingShort = StripWhitespace(sibling.WorkspaceName);
                var siblingCtx = new MustacheContext(
                    Name: sibling.WorkspaceName,
                    WorkspaceName: sibling.WorkspaceName,
                    ShortName: siblingShort,
                    ModuleName: sibling.WorkspaceName,
                    Publisher: template.Defaults.Publisher,
                    ExtensionPrefix: string.Empty,
                    Affix: template.Defaults.AffixType == AffixType.None ? string.Empty : template.Defaults.Affix,
                    FolderPath: string.Empty);
                WriteString(archive, workspaceFile,
                    BuildCodeWorkspace(
                        orgConfig.Settings.CodeWorkspaceJson,
                        template.CodeWorkspaceJson,
                        existing,
                        siblingCtx));
                fileCount++;
            }
        }

        stream.Position = 0;
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
            .FirstOrDefaultAsync(ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["TemplateKey"] = $"Template '{key}' was not found.",
            });

        // Folder tree hydration is delegated to TemplateService so workspace
        // generation, template authoring loads, and cross-org imports share
        // one implementation (#77).
        await _templates.HydrateExtensionFolderTreeAsync(new[] { template }, ct);
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

        await _templates.HydrateModuleExtensionFolderTreeAsync(modules, ct);

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
    private static List<EmittableExtension> BuildExtensionList(RuntimeTemplate template, ProjectPlan plan, IReadOnlyList<Module> modules)
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

            list.Add(BuildFromTemplate(ext, template, plan, from, to));
        }

        foreach (var module in modules)
        {
            var size = module.IdRangeSize ?? template.ModuleIdRangeSize;
            var from = cursor;
            var to = from + size - 1;
            cursor = to + 1;
            list.Add(BuildFromModule(module, template, plan, from, to));
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

    private static EmittableExtension BuildFromTemplate(WorkspaceExtension ext, RuntimeTemplate template, ProjectPlan plan, int from, int to)
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
            Publisher: template.Defaults.Publisher,
            IsModuleClone: false,
            ModuleKey: null,
            ModuleName: name,
            FolderRoots: BuildFolderTree(ext.Folders),
            Dependencies: ext.Dependencies
                .OrderBy(d => d.Ordering)
                .Select(d => new EmittableDependency(d.RefExtensionPath, d.RefModuleKey, d.LitId, d.LitName, d.LitPublisher, d.LitVersion))
                .ToList());
    }

    private static EmittableExtension BuildFromModule(Module module, RuntimeTemplate template, ProjectPlan plan, int from, int to)
    {
        // Module-cloned extension name defaults to "{{extension_prefix}} {module.name}".
        // The substitution happens up-front so the resolved name is stable for
        // downstream dep resolution.
        var nameTemplate = $"{{{{extension_prefix}}}} {module.Name}";
        var name = SubstituteScalar(nameTemplate, plan, template);

        // Module dependencies (from module_dependencies) become literal deps.
        // Implicit dependencies on every required template-declared extension
        // get added in BuildAppJson at emit time so they pick up the
        // freshly-generated GUIDs from the rest of the list.
        var deps = module.Dependencies
            .OrderBy(d => d.Ordering)
            .Select(d => new EmittableDependency(null, null, d.DepId, d.DepName, d.DepPublisher, d.DepVersion))
            .ToList();

        return new EmittableExtension(
            Path: module.Key,
            Name: name,
            Id: Guid.NewGuid(),
            IdRangeFrom: from,
            IdRangeTo: to,
            Application: plan.ApplicationVersion,
            Runtime: plan.RuntimeVersion,
            Publisher: template.Defaults.Publisher,
            IsModuleClone: true,
            ModuleKey: module.Key,
            ModuleName: module.Name,
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

    // ===== Per-extension emission =====

    private int WriteExtension(
        ZipArchive archive,
        string rootFolder,
        EmittableExtension ext,
        IReadOnlyList<EmittableExtension> allExtensions,
        RuntimeTemplate template,
        ProjectPlan plan)
    {
        var extPath = $"{rootFolder}/{ext.Path}";
        var appJson = BuildAppJson(ext, allExtensions, template, plan);
        WriteString(archive, $"{extPath}/app.json", appJson);
        var fileCount = 1;

        if (template.AppSourceCop.Include)
        {
            WriteString(archive, $"{extPath}/AppSourceCop.json", BuildAppSourceCopJson(template.AppSourceCop));
            fileCount++;
        }

        var substitutionCtx = new MustacheContext(
            Name: ext.Name,
            WorkspaceName: plan.WorkspaceName,
            ShortName: StripWhitespace(plan.WorkspaceName),
            ModuleName: ext.ModuleName,
            Publisher: ext.Publisher,
            ExtensionPrefix: plan.ExtensionPrefix,
            Affix: template.Defaults.AffixType == AffixType.None ? string.Empty : template.Defaults.Affix,
            FolderPath: string.Empty);

        fileCount += EmitFolderTree(archive, extPath, ext.FolderRoots, plan.IncludeExamples, substitutionCtx);
        return fileCount;
    }

    /// <summary>
    /// Recursively emit every folder + file under <paramref name="parentPath"/>.
    /// A folder with no files (after the example filter) and no sub-folders
    /// gets a <c>.gitkeep</c> so the ZIP carries the structure.
    /// </summary>
    private int EmitFolderTree(
        ZipArchive archive,
        string parentPath,
        IReadOnlyList<FolderNode> folders,
        bool includeExamples,
        MustacheContext baseCtx)
    {
        var fileCount = 0;
        foreach (var folder in folders)
        {
            var folderPath = $"{parentPath}/{folder.Path}";
            var emittableFiles = folder.Files
                .Where(f => includeExamples || !f.IsExample)
                .ToList();

            var folderCtx = baseCtx with
            {
                FolderPath = baseCtx.FolderPath.Length == 0 ? folder.Path : $"{baseCtx.FolderPath}/{folder.Path}",
            };

            foreach (var file in emittableFiles)
            {
                var dest = $"{folderPath}/{file.Path}";
                var content = file.Path.EndsWith(".al", StringComparison.OrdinalIgnoreCase)
                    ? SubstituteMustache(file.Content, folderCtx)
                    : file.Content;
                WriteString(archive, dest, content);
                fileCount++;
            }

            fileCount += EmitFolderTree(archive, folderPath, folder.Folders, includeExamples, folderCtx);

            // Empty leaf: drop a .gitkeep so the folder shows up in git.
            if (emittableFiles.Count == 0 && folder.Folders.Count == 0)
            {
                WriteEmptyFile(archive, $"{folderPath}/.gitkeep");
                fileCount++;
            }
        }
        return fileCount;
    }

    // ===== app.json builders =====

    /// <summary>
    /// Builds the per-extension <c>AppSourceCop.json</c> contents. The
    /// <see cref="AppSourceCopSettings.Include"/> flag is stripped — it's our
    /// authoring toggle, not an AL concept; AL would reject an unknown field.
    /// </summary>
    private static string BuildAppSourceCopJson(AppSourceCopSettings settings)
    {
        var node = new JsonObject
        {
            ["mandatoryPrefix"] = settings.MandatoryPrefix,
            ["supportedCountries"] = new JsonArray(settings.SupportedCountries.Select(c => (JsonNode)c).ToArray()),
        };
        return SerializeIndented(node);
    }

    private string BuildAppJson(
        EmittableExtension ext,
        IReadOnlyList<EmittableExtension> allExtensions,
        RuntimeTemplate template,
        ProjectPlan plan)
    {
        var node = BaseAppJson(template);
        node["id"] = ext.Id.ToString();
        node["name"] = ext.Name;
        node["publisher"] = ext.Publisher;
        node["brief"] = plan.Brief;
        node["description"] = plan.Description;
        node["version"] = "0.0.0.1";
        node["application"] = ext.Application;
        node["platform"] = template.Defaults.Platform;
        node["runtime"] = ext.Runtime;
        node["idRanges"] = new JsonArray(new JsonObject
        {
            ["from"] = ext.IdRangeFrom,
            ["to"] = ext.IdRangeTo,
        });
        node["dependencies"] = ResolveDependencies(ext, allExtensions);
        return SerializeIndented(node);
    }

    /// <summary>
    /// Walks an extension's declared deps in order, dispatching on which
    /// reference shape is set. Intra-workspace refs lock onto the
    /// freshly-generated GUIDs of the in-this-build extensions so AL's
    /// dependency graph resolves at compile time. Module-key refs work the
    /// same way when the target module was selected for this workspace,
    /// otherwise they fall back to the catalogue's stored dep identifiers.
    /// Module-cloned extensions also get an implicit dependency on every
    /// required template extension so a Document Capture clone depends on
    /// Core without the template author having to spell it out.
    /// </summary>
    private JsonArray ResolveDependencies(EmittableExtension ext, IReadOnlyList<EmittableExtension> allExtensions)
    {
        var array = new JsonArray();
        var emitted = new HashSet<Guid>();

        // Module clones get implicit dependencies on required template
        // extensions (typically just Core). Emitted before the module's own
        // deps so Core is listed first in the resulting app.json.
        if (ext.IsModuleClone)
        {
            foreach (var other in allExtensions)
            {
                if (other.IsModuleClone) continue;
                if (other.Id == ext.Id) continue;
                if (!emitted.Add(other.Id)) continue;
                array.Add(BuildDepNode(other.Id.ToString(), other.Name, other.Publisher, "0.0.0.1"));
            }
        }

        foreach (var dep in ext.Dependencies)
        {
            if (dep.RefExtensionPath is not null)
            {
                var target = allExtensions.FirstOrDefault(e => string.Equals(e.Path, dep.RefExtensionPath, StringComparison.Ordinal));
                if (target is null)
                {
                    // The template-save validator should catch this; if it
                    // slipped through, surface a clear runtime error rather
                    // than emit a half-formed dep node.
                    throw new PlanValidationException(new Dictionary<string, string>
                    {
                        [$"Extensions[{ext.Path}].Dependencies"] =
                            $"Extension '{ext.Path}' depends on extension '{dep.RefExtensionPath}', which isn't part of this workspace.",
                    });
                }
                if (!emitted.Add(target.Id)) continue;
                array.Add(BuildDepNode(target.Id.ToString(), target.Name, target.Publisher, "0.0.0.1"));
            }
            else if (dep.RefModuleKey is not null)
            {
                // Module-cloned-in-this-workspace: prefer the freshly-allocated
                // identity. Otherwise emit a literal pointing at the catalogue
                // module's dep_* fields if we still have them (we don't — the
                // module catalogue carries its own dependencies, not a self-
                // identifying GUID). Without a self GUID on Module, the
                // best we can do here is emit nothing, but the validator
                // shouldn't permit a module ref to a module that isn't either
                // selected or self-described — call out the gap.
                var target = allExtensions.FirstOrDefault(e => e.IsModuleClone && string.Equals(e.ModuleKey, dep.RefModuleKey, StringComparison.Ordinal));
                if (target is not null)
                {
                    if (!emitted.Add(target.Id)) continue;
                    array.Add(BuildDepNode(target.Id.ToString(), target.Name, target.Publisher, "0.0.0.1"));
                }
                // else: the validator should have caught this; skip silently
                // to avoid emitting a placeholder GUID that won't compile.
            }
            else if (dep.LitId is not null)
            {
                array.Add(BuildDepNode(dep.LitId, dep.LitName ?? string.Empty, dep.LitPublisher ?? string.Empty, dep.LitVersion ?? string.Empty));
            }
        }
        return array;
    }

    private static JsonObject BuildDepNode(string id, string name, string publisher, string version) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["publisher"] = publisher,
        ["version"] = version,
    };

    private static string BuildStandaloneAppJson(StandaloneExtensionPlan plan, RuntimeTemplate template)
    {
        var node = BaseAppJson(template);
        node["id"] = Guid.NewGuid().ToString();
        node["name"] = plan.ExtensionName;
        node["publisher"] = string.IsNullOrWhiteSpace(plan.Publisher) ? template.Defaults.Publisher : plan.Publisher;
        node["brief"] = plan.Brief;
        node["description"] = plan.Description;
        node["version"] = "0.0.0.1";
        node["application"] = plan.ApplicationVersion;
        node["platform"] = template.Defaults.Platform;
        node["runtime"] = plan.RuntimeVersion;
        node["idRanges"] = new JsonArray(new JsonObject
        {
            ["from"] = plan.IdRangeFrom,
            ["to"] = plan.IdRangeTo,
        });

        var deps = new JsonArray();
        foreach (var d in plan.Dependencies)
        {
            deps.Add(BuildDepNode(d.DepId, d.DepName, d.DepPublisher, d.DepVersion));
        }
        node["dependencies"] = deps;
        return SerializeIndented(node);
    }

    private static JsonObject BaseAppJson(RuntimeTemplate template)
    {
        var node = new JsonObject
        {
            ["id"] = "00000000-0000-0000-0000-000000000000",
            ["name"] = string.Empty,
        };
        // Round-trip TemplateDefaults through JSON so the template-defined
        // fields (target, features, supportedLocales, resourceExposurePolicy,
        // …) get merged. The form-pre-fill fields (application, platform,
        // extension_prefix, affix, affixType) appear in the serialised blob
        // too — the per-extension layering overwrites application / platform
        // with plan values; the affix / extension_prefix entries are
        // mustache-template inputs, not app.json content.
        var defaults = JsonSerializer.SerializeToNode(template.Defaults, JsonOptions)?.AsObject();
        if (defaults is not null)
        {
            foreach (var kvp in defaults.ToList())
            {
                // Strip the form-pre-fill keys that aren't app.json fields.
                if (kvp.Key is "application" or "platform" or "extension_prefix" or "affix" or "affixType") continue;
                node[kvp.Key] = kvp.Value?.DeepClone();
            }
        }
        return node;
    }

    // ===== Org assets =====

    private static int WriteOrgLogo(ZipArchive archive, string parentPath, OrganizationAsset? logo)
    {
        if (logo is null) return 0;
        var ext = logo.ContentType switch
        {
            "image/svg+xml" => "svg",
            _ => "png",
        };
        var entry = archive.CreateEntry($"{parentPath}/logo.{ext}", CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(logo.Content, 0, logo.Content.Length);
        return 1;
    }

    private int WriteOrgFiles(
        ZipArchive archive,
        string rootFolder,
        IReadOnlyList<OrganizationFile> files,
        ProjectPlan plan,
        RuntimeTemplate template)
    {
        if (files.Count == 0) return 0;
        var written = 0;
        var ctx = new MustacheContext(
            Name: plan.WorkspaceName,
            WorkspaceName: plan.WorkspaceName,
            ShortName: StripWhitespace(plan.WorkspaceName),
            ModuleName: plan.WorkspaceName,
            Publisher: template.Defaults.Publisher,
            ExtensionPrefix: plan.ExtensionPrefix,
            Affix: template.Defaults.AffixType == AffixType.None ? string.Empty : template.Defaults.Affix,
            FolderPath: string.Empty);
        foreach (var file in files)
        {
            var content = file.MustacheEnabled
                ? SubstituteMustache(file.Content, ctx with { FolderPath = file.Path })
                : file.Content;
            WriteString(archive, $"{rootFolder}/{file.Path}", content);
            written++;
        }
        return written;
    }

    // ===== Workspace-level files =====

    /// <summary>
    /// Builds <c>{ShortName}.code-workspace</c> by layering three sources
    /// (Issue #61):
    /// <list type="number">
    ///   <item>The organisation base JSON template
    ///         (<see cref="OrganizationSettings.CodeWorkspaceJson"/>).</item>
    ///   <item>The optional per-template overlay
    ///         (<see cref="RuntimeTemplate.CodeWorkspaceJson"/>) — deep-merged
    ///         on the <c>settings</c> object, replacing wholesale on every
    ///         other top-level key.</item>
    ///   <item>The computed <c>folders</c> array, written last and always
    ///         authoritative — the workspace must point at the folders the
    ///         generator actually emits, regardless of what either layer
    ///         pasted.</item>
    /// </list>
    /// Mustache substitution runs over each layer before merging so both can
    /// use <c>{{publisher}}</c>, <c>{{shortName}}</c>, etc.
    /// </summary>
    private string BuildCodeWorkspace(
        string orgJsonTemplate,
        string? templateJsonOverlay,
        IReadOnlyList<string> folderPaths,
        MustacheContext ctx)
    {
        var root = ParseSubstitutedJsonObject(orgJsonTemplate, ctx, "codeWorkspaceJson");

        if (!string.IsNullOrWhiteSpace(templateJsonOverlay))
        {
            var overlay = ParseSubstitutedJsonObject(templateJsonOverlay, ctx, "template.codeWorkspaceJson");
            MergeTemplateOverlay(root, overlay);
        }

        var folders = new JsonArray();
        foreach (var path in folderPaths) folders.Add(new JsonObject { ["path"] = path });
        root["folders"] = folders;
        return SerializeIndented(root);
    }

    /// <summary>
    /// Substitute mustache vars and parse the result as a JSON object. Validation
    /// errors are keyed under <paramref name="fieldKey"/> so the workspace and
    /// template error surfaces stay distinct in the audit log and the form.
    /// </summary>
    private JsonObject ParseSubstitutedJsonObject(string source, MustacheContext ctx, string fieldKey)
    {
        var substituted = SubstituteMustache(source, ctx);
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(substituted);
        }
        catch (JsonException ex)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                [fieldKey] = $"Workspace JSON template did not parse: {ex.Message}",
            });
        }
        if (parsed is not JsonObject root)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                [fieldKey] = "Workspace JSON template must be a JSON object.",
            });
        }
        return root;
    }

    /// <summary>
    /// Merge the per-template overlay onto the org base in place:
    /// <list type="bullet">
    ///   <item><c>folders</c>: skipped — the generator owns that key and writes
    ///         it last regardless of either layer.</item>
    ///   <item><c>settings</c>: when both layers carry a JSON object, the
    ///         overlay's keys win individually so a template can add one new
    ///         AL/VS Code setting without restating the org block.</item>
    ///   <item>everything else: the overlay replaces the org's value wholesale.
    ///         This keeps semantics predictable for arbitrarily-shaped keys
    ///         like <c>tasks</c> or <c>launch</c> without inventing
    ///         JSON-Patch-style merge rules.</item>
    /// </list>
    /// </summary>
    private static void MergeTemplateOverlay(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay.ToList())
        {
            if (string.Equals(key, "folders", StringComparison.Ordinal)) continue;

            if (string.Equals(key, "settings", StringComparison.Ordinal)
                && value is JsonObject overlaySettings
                && target.TryGetPropertyValue("settings", out var targetSettingsNode)
                && targetSettingsNode is JsonObject targetSettings)
            {
                foreach (var (sk, sv) in overlaySettings.ToList())
                {
                    targetSettings[sk] = sv?.DeepClone();
                }
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    private static string BuildReadme(ProjectPlan plan) =>
        $"""
        # {plan.WorkspaceName}

        {plan.Description}

        Generated by AL Dev Toolbox.
        """;

    // ===== Mustache substitution =====

    /// <summary>
    /// Substitutes the supported placeholders inside <paramref name="source"/>.
    /// The table is: <c>{{name}}</c>, <c>{{workspaceName}}</c>,
    /// <c>{{shortName}}</c>, <c>{{moduleName}}</c>, <c>{{publisher}}</c>,
    /// <c>{{extension_prefix}}</c>, <c>{{affix}}</c>, <c>{{namespace}}</c>,
    /// <c>{{guid}}</c>. Unknown keys log a warning and pass through verbatim.
    /// </summary>
    private string SubstituteMustache(string source, MustacheContext ctx) =>
        MustacheRegex.Replace(source, match =>
        {
            var key = match.Groups[1].Value;
            return key switch
            {
                "name" => ctx.Name,
                "workspaceName" => ctx.WorkspaceName,
                "shortName" => ctx.ShortName,
                "moduleName" => ctx.ModuleName,
                "publisher" => ctx.Publisher,
                "extension_prefix" => ctx.ExtensionPrefix,
                "affix" => ctx.Affix,
                "namespace" => ctx.FolderPath.Replace('/', '.'),
                "guid" => Guid.NewGuid().ToString(),
                _ => UnknownVariable(match.Value, key),
            };
        });

    private string UnknownVariable(string original, string key)
    {
        _logger.LogWarning("Unknown mustache variable {{{{{Key}}}}} encountered during generation; left as-is.", key);
        return original;
    }

    /// <summary>
    /// Scalar substitution used for the extension name (no folder-context
    /// awareness yet — names are built before folder traversal). Delegates
    /// to <see cref="SubstituteMustache"/> with an empty FolderPath so
    /// <c>{{namespace}}</c> resolves to the empty string; #81 collapses the
    /// previously duplicated switch.
    /// </summary>
    private string SubstituteScalar(string source, ProjectPlan plan, RuntimeTemplate template)
    {
        var ctx = new MustacheContext(
            Name: source,
            WorkspaceName: plan.WorkspaceName,
            ShortName: StripWhitespace(plan.WorkspaceName),
            ModuleName: source,
            Publisher: template.Defaults.Publisher,
            ExtensionPrefix: plan.ExtensionPrefix,
            Affix: template.Defaults.AffixType == AffixType.None ? string.Empty : template.Defaults.Affix,
            FolderPath: string.Empty);
        return SubstituteMustache(source, ctx);
    }

    private record MustacheContext(
        string Name,
        string WorkspaceName,
        string ShortName,
        string ModuleName,
        string Publisher,
        string ExtensionPrefix,
        string Affix,
        string FolderPath);

    // ===== ZIP helpers =====

    private static void WriteString(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void WriteEmptyFile(ZipArchive archive, string path)
    {
        archive.CreateEntry(path, CompressionLevel.NoCompression).Open().Dispose();
    }

    private static async Task WriteEmbeddedAsync(ZipArchive archive, string path, string resourceName, CancellationToken ct)
    {
        var assembly = typeof(GenerationService).Assembly;
        await using var resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        await resource.CopyToAsync(entryStream, ct);
    }

    private static string SerializeIndented(JsonNode node) =>
        JsonSerializer.Serialize(node, JsonOptions);

    private static string StripWhitespace(string value) =>
        Regex.Replace(value ?? string.Empty, @"\s+", string.Empty);

    // ===== In-memory shapes =====

    /// <summary>Resolved per-extension data carried through the generation pipeline.</summary>
    private sealed record EmittableExtension(
        string Path,
        string Name,
        Guid Id,
        int IdRangeFrom,
        int IdRangeTo,
        string Application,
        string Runtime,
        string Publisher,
        bool IsModuleClone,
        string? ModuleKey,
        string ModuleName,
        IReadOnlyList<FolderNode> FolderRoots,
        IReadOnlyList<EmittableDependency> Dependencies);

    /// <summary>Folder + its files + its children (recursive). Built once at load time.</summary>
    private sealed record FolderNode(string Path, IReadOnlyList<FileLeaf> Files, IReadOnlyList<FolderNode> Folders);

    private sealed record FileLeaf(string Path, string Content, bool IsExample);

    private sealed record EmittableDependency(
        string? RefExtensionPath,
        string? RefModuleKey,
        string? LitId,
        string? LitName,
        string? LitPublisher,
        string? LitVersion);
}

/// <summary>Container for a finished archive. The stream is rewound and ready to copy to the HTTP response body.</summary>
public record GeneratedArchive(MemoryStream Stream, string FileName);

/// <summary>Sibling-extension context for the New Extension flow.</summary>
public record SiblingWorkspaceContext(
    string WorkspaceName,
    IReadOnlyList<string> ModuleKeys,
    IReadOnlyList<string> ExistingFolders);
