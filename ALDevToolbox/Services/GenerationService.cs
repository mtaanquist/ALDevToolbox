using System.Diagnostics;
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
/// Builds an in-memory ZIP archive for a workspace or standalone extension. The
/// caller streams the resulting bytes to the HTTP response — see
/// <c>generation-engine.md</c> for the full algorithm.
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

    /// <summary>The ForNAV publisher value used when matching catalogue rows for the "include ForNAV" option.</summary>
    private const string ForNavPublisher = "ForNAV";

    /// <summary>The fixed <c>settings</c> block written into <c>.code-workspace</c>.</summary>
    private const string WorkspaceSettingsJson = """
{
  "editor.formatOnSave": true,
  "editor.autoIndent": "full",
  "editor.detectIndentation": false,
  "editor.tabSize": 4,
  "editor.insertSpaces": true,
  "al.codeAnalyzers": [
    "${CodeCop}",
    "${AppSourceCop}",
    "${UICop}"
  ],
  "al.enableCodeAnalysis": true,
  "al.ruleSetPath": "../.assets/rulesets/Company.ruleset.json"
}
""";

    private readonly AppDbContext _db;
    private readonly WorkspaceConfigService _config;
    private readonly ILogger<GenerationService> _logger;

    public GenerationService(AppDbContext db, WorkspaceConfigService config, ILogger<GenerationService> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    // ===== Workspace flow =====

    /// <summary>
    /// Generates a workspace ZIP for the given plan. The returned stream is a
    /// rewound <see cref="MemoryStream"/> ready to be copied to the HTTP body.
    /// </summary>
    public async Task<GeneratedArchive> GenerateWorkspaceAsync(ProjectPlan plan, CancellationToken ct = default)
    {
        ValidateWorkspacePlan(plan);

        var stopwatch = Stopwatch.StartNew();
        var template = await _db.RuntimeTemplates
            .AsNoTracking()
            .Include(t => t.Folders.OrderBy(f => f.Ordering))
                .ThenInclude(f => f.Files.OrderBy(x => x.Ordering))
            .Include(t => t.ModuleFolders.OrderBy(f => f.Ordering))
                .ThenInclude(f => f.Files.OrderBy(x => x.Ordering))
            .Where(t => t.DeletedAt == null && t.Key == plan.TemplateKey)
            .FirstOrDefaultAsync(ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                [nameof(plan.TemplateKey)] = $"Template '{plan.TemplateKey}' was not found.",
            });

        var modules = await LoadSelectedModulesAsync(plan.SelectedModuleKeys, ct);

        var shortName = StripWhitespace(plan.WorkspaceName);
        var rootFolder = shortName;
        var coreId = Guid.NewGuid();
        var coreName = $"{plan.WorkspaceName} Core";

        // Compute module names + folder names + GUIDs up front so the
        // workspace file and the per-module dependency graphs all line up.
        var moduleInfos = AssignModuleRanges(template, modules, plan, coreId, coreName);

        var stream = new MemoryStream();
        var fileCount = 0;

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // .assets — logo + ruleset
            await WriteEmbeddedAsync(archive, $"{rootFolder}/.assets/images/logo.png", "ALDevToolbox.Resources.logo.png", ct);
            await WriteEmbeddedAsync(archive, $"{rootFolder}/.assets/rulesets/Company.ruleset.json", "ALDevToolbox.Resources.Company.ruleset.json", ct);
            fileCount += 2;

            // Core extension uses the Core folder list; modules use the module
            // folder list so Core's per-extension scaffolding (App Install
            // codeunits, setup tables, permission sets) doesn't get duplicated
            // into every module ZIP.
            var coreFolders = SnapshotCoreFolders(template);
            var moduleFolders = SnapshotModuleFolders(template);

            // Core extension
            var coreAppJson = await BuildCoreAppJsonAsync(template, plan, coreId, coreName, ct);
            fileCount += await WriteExtensionAsync(
                archive,
                rootRelative: $"{rootFolder}/Core",
                appJson: coreAppJson,
                template: template,
                folders: coreFolders,
                plan: plan,
                extensionName: coreName,
                moduleName: coreName,
                ct: ct);

            // Module extensions
            foreach (var info in moduleInfos)
            {
                var moduleAppJson = BuildModuleAppJson(info, template, plan);
                fileCount += await WriteExtensionAsync(
                    archive,
                    rootRelative: $"{rootFolder}/{info.FolderName}",
                    appJson: moduleAppJson,
                    template: template,
                    folders: moduleFolders,
                    plan: plan,
                    extensionName: info.ExtensionName,
                    moduleName: info.Module.Name,
                    ct: ct);
            }

            // Root files
            await WriteEmbeddedAsync(archive, $"{rootFolder}/.gitignore", "ALDevToolbox.Resources.al.gitignore", ct);
            var workspaceFolderList = new List<string> { "Core" };
            workspaceFolderList.AddRange(moduleInfos.Select(m => m.FolderName));
            WriteString(archive, $"{rootFolder}/{shortName}.code-workspace", BuildCodeWorkspace(workspaceFolderList));
            WriteString(archive, $"{rootFolder}/README.md", BuildReadme(plan));
            // Save the form-post shape plus per-extension identities (Core +
            // every module) alongside the ZIP so a sibling-extension import
            // later can declare real dependencies on these GUIDs and avoid id
            // range collisions. See Milestone P2.3 in .design/milestones.md.
            var identities = BuildExtensionIdentities(template, plan, coreId, coreName, moduleInfos);
            WriteString(archive, $"{rootFolder}/{WorkspaceConfigService.FileName}", _config.BuildWorkspace(plan, identities));
            fileCount += 4;
        }

        stream.Position = 0;
        stopwatch.Stop();

        _logger.LogInformation(
            "Generated workspace '{Workspace}' from template '{Template}' with modules [{Modules}]: {Files} files, {Bytes} bytes, {Ms} ms.",
            plan.WorkspaceName,
            plan.TemplateKey,
            string.Join(",", plan.SelectedModuleKeys),
            fileCount,
            stream.Length,
            stopwatch.ElapsedMilliseconds);

        return new GeneratedArchive(stream, $"{shortName}.zip");
    }

    // ===== Standalone / sibling extension flow =====

    /// <summary>
    /// Generates an extension ZIP for the New Extension flow. With
    /// <paramref name="sibling"/> non-null, the ZIP also carries an updated
    /// <c>&lt;WorkspaceName&gt;.code-workspace</c> at archive root listing
    /// every existing extension plus the new one — see Milestone P2.3 in
    /// <c>.design/milestones.md</c> for the sibling-extension UX.
    /// </summary>
    public async Task<GeneratedArchive> GenerateExtensionAsync(
        StandaloneExtensionPlan plan,
        SiblingWorkspaceContext? sibling = null,
        CancellationToken ct = default)
    {
        ValidateExtensionPlan(plan);

        var stopwatch = Stopwatch.StartNew();
        var template = await _db.RuntimeTemplates
            .AsNoTracking()
            .Include(t => t.Folders.OrderBy(f => f.Ordering))
                .ThenInclude(f => f.Files.OrderBy(x => x.Ordering))
            .Include(t => t.ModuleFolders.OrderBy(f => f.Ordering))
                .ThenInclude(f => f.Files.OrderBy(x => x.Ordering))
            .Where(t => t.DeletedAt == null && t.Key == plan.TemplateKey)
            .FirstOrDefaultAsync(ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                [nameof(plan.TemplateKey)] = $"Template '{plan.TemplateKey}' was not found.",
            });

        // Resolve the imported workspace's modules up front so we can build a
        // .code-workspace folder list that matches the existing on-disk layout.
        // Unknown / soft-deleted module keys are dropped silently, mirroring
        // the import-side validation that already gated this submission.
        var siblingModules = sibling is null
            ? new List<Module>()
            : await LoadSelectedModulesAsync(sibling.ModuleKeys, ct);

        var stream = new MemoryStream();
        var fileCount = 0;
        var folderName = StripWhitespace(plan.ExtensionName);

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Standalone extensions reuse the Core folder list — they're a
            // primary, top-level extension by definition, not a module living
            // alongside a Core in someone else's workspace. The sibling-extension
            // flow doesn't auto-wire dependencies on the workspace's Core or
            // modules: the workspace config doesn't carry their GUIDs, and we
            // shouldn't fabricate IDs that won't match the existing app.json
            // files. Users add those references by hand from their workspace.
            var appJson = BuildStandaloneAppJson(template, plan);
            fileCount += await WriteExtensionAsync(
                archive,
                rootRelative: folderName,
                appJson: appJson,
                template: template,
                folders: SnapshotCoreFolders(template),
                plan: PlanForStandaloneSubstitution(plan),
                extensionName: plan.ExtensionName,
                moduleName: plan.ExtensionName,
                publisherOverride: plan.Publisher,
                ct: ct);

            // Mirror the workspace flow: save the form-post shape alongside the
            // ZIP so the user can re-import the same settings later. The file
            // sits inside the extension folder rather than at archive root so
            // it tags along with the extension when the user drops the folder
            // into an existing workspace.
            WriteString(archive, $"{folderName}/{WorkspaceConfigService.FileName}", _config.BuildExtension(plan));
            fileCount++;

            // Sibling-extension flow: regenerate the workspace's .code-workspace
            // with the new folder appended so the user can drop it straight
            // into the existing repo without hand-editing the folders array.
            if (sibling is not null)
            {
                var workspaceFile = $"{StripWhitespace(sibling.WorkspaceName)}.code-workspace";
                // Prefer folder names captured at the original generation time
                // (carried in the imported config's identities); fall back to
                // a DB lookup only when older configs lacked the section.
                var workspaceFolders = sibling.ExistingFolders.Count > 0
                    ? sibling.ExistingFolders.ToList()
                    : new[] { "Core" }
                        .Concat(siblingModules.Select(m => StripWhitespace(m.Name)))
                        .ToList();
                workspaceFolders.Add(folderName);
                WriteString(archive, workspaceFile, BuildCodeWorkspace(workspaceFolders));
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

    // ===== Per-extension writer =====

    /// <summary>
    /// Emits one extension folder (Core or module or standalone). Returns the
    /// number of files written so the caller can roll up totals for logging.
    /// File contents come from the folder's <see cref="TemplateFolder.Files"/>;
    /// mustache substitution runs on every <c>.al</c> file before it's written.
    /// </summary>
    private Task<int> WriteExtensionAsync(
        ZipArchive archive,
        string rootRelative,
        string appJson,
        RuntimeTemplate template,
        IReadOnlyList<FolderEmit> folders,
        ProjectPlan plan,
        string extensionName,
        string moduleName,
        string? publisherOverride = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        WriteString(archive, $"{rootRelative}/app.json", appJson);
        WriteString(archive, $"{rootRelative}/AppSourceCop.json", JsonSerializer.Serialize(template.AppSourceCop, JsonOptions));
        var fileCount = 2;

        // Top-level folders the caller's folder list already declares
        // (case-insensitive), so the static fallback folders below don't
        // collide on Windows extraction. Without this, a folder list that
        // lays out 'translations' would extract alongside our hardcoded
        // 'Translations/.gitkeep' and produce two folders that resolve to
        // the same path.
        var declaredTops = new HashSet<string>(
            folders.Select(f => f.Path.Split('/', 2)[0]),
            StringComparer.OrdinalIgnoreCase);
        if (!declaredTops.Contains("libs"))
        {
            WriteEmptyFile(archive, $"{rootRelative}/libs/.gitkeep");
            fileCount++;
        }
        if (!declaredTops.Contains("permissionsets"))
        {
            WriteEmptyFile(archive, $"{rootRelative}/permissionsets/.gitkeep");
            fileCount++;
        }
        if (!declaredTops.Contains("Translations"))
        {
            WriteEmptyFile(archive, $"{rootRelative}/Translations/.gitkeep");
            fileCount++;
        }

        foreach (var folder in folders)
        {
            var folderRelative = $"{rootRelative}/{folder.Path}";

            if (plan.IncludeExamples && folder.Files.Count > 0)
            {
                foreach (var file in folder.Files)
                {
                    var destInZip = $"{folderRelative}/{file.Path}";
                    if (file.Path.EndsWith(".al", StringComparison.OrdinalIgnoreCase))
                    {
                        var substituted = SubstituteMustache(file.Content, new MustacheContext(
                            Name: extensionName,
                            WorkspaceName: plan.WorkspaceName,
                            ShortName: StripWhitespace(plan.WorkspaceName),
                            ModuleName: moduleName,
                            Publisher: publisherOverride ?? template.Defaults.Publisher,
                            Prefix: template.AppSourceCop.MandatoryPrefix,
                            FolderPath: folder.Path));
                        WriteString(archive, destInZip, substituted);
                    }
                    else
                    {
                        WriteString(archive, destInZip, file.Content);
                    }
                    fileCount++;
                }
            }
            else
            {
                WriteEmptyFile(archive, $"{folderRelative}/.gitkeep");
                fileCount++;
            }
        }

        return Task.FromResult(fileCount);
    }

    /// <summary>
    /// Snapshot of a folder + its files, decoupled from whether the source is
    /// <see cref="TemplateFolder"/> (Core) or <see cref="TemplateModuleFolder"/>
    /// (modules). Both flow through <see cref="WriteExtensionAsync"/> the same way.
    /// </summary>
    private readonly record struct FolderEmit(string Path, IReadOnlyList<FileEmit> Files);

    private readonly record struct FileEmit(string Path, string Content);

    /// <summary>Snapshots the Core folder list for emission into the Core extension ZIP.</summary>
    private static IReadOnlyList<FolderEmit> SnapshotCoreFolders(RuntimeTemplate template) =>
        template.Folders.OrderBy(f => f.Ordering)
            .Select(f => new FolderEmit(
                f.Path,
                f.Files.OrderBy(x => x.Ordering)
                    .Select(x => new FileEmit(x.Path, x.Content))
                    .ToList()))
            .ToList();

    /// <summary>Snapshots the module folder list for emission into every module extension ZIP.</summary>
    private static IReadOnlyList<FolderEmit> SnapshotModuleFolders(RuntimeTemplate template) =>
        template.ModuleFolders.OrderBy(f => f.Ordering)
            .Select(f => new FolderEmit(
                f.Path,
                f.Files.OrderBy(x => x.Ordering)
                    .Select(x => new FileEmit(x.Path, x.Content))
                    .ToList()))
            .ToList();

    // ===== app.json builders =====

    private async Task<string> BuildCoreAppJsonAsync(
        RuntimeTemplate template,
        ProjectPlan plan,
        Guid coreId,
        string coreName,
        CancellationToken ct)
    {
        var node = BaseAppJson(template);
        node["id"] = coreId.ToString();
        node["name"] = coreName;
        node["publisher"] = template.Defaults.Publisher;
        node["brief"] = plan.Brief;
        node["description"] = plan.Description;
        node["version"] = "0.0.0.1";
        node["application"] = plan.ApplicationVersion;
        node["platform"] = template.DefaultPlatform;
        node["runtime"] = plan.RuntimeVersion;
        node["idRanges"] = new JsonArray(new JsonObject
        {
            ["from"] = plan.CoreIdRangeFrom,
            ["to"] = plan.CoreIdRangeTo,
        });
        var coreDeps = new JsonArray();
        if (plan.IncludeForNav)
        {
            foreach (var d in await LoadForNavDependenciesAsync(ct)) coreDeps.Add(d);
        }
        node["dependencies"] = coreDeps;
        return SerializeIndented(node);
    }

    private static string BuildModuleAppJson(ModuleAssignment info, RuntimeTemplate template, ProjectPlan plan)
    {
        var node = BaseAppJson(template);
        node["id"] = info.ExtensionId.ToString();
        node["name"] = info.ExtensionName;
        node["publisher"] = template.Defaults.Publisher;
        node["brief"] = plan.Brief;
        node["description"] = plan.Description;
        node["version"] = "0.0.0.1";
        node["application"] = plan.ApplicationVersion;
        node["platform"] = template.DefaultPlatform;
        node["runtime"] = plan.RuntimeVersion;
        node["idRanges"] = new JsonArray(new JsonObject
        {
            ["from"] = info.IdRangeFrom,
            ["to"] = info.IdRangeTo,
        });

        var deps = new JsonArray
        {
            new JsonObject
            {
                ["id"] = info.CoreId.ToString(),
                ["name"] = info.CoreName,
                ["publisher"] = template.Defaults.Publisher,
                ["version"] = "0.0.0.1",
            },
        };
        foreach (var d in info.Module.Dependencies.OrderBy(d => d.Ordering))
        {
            deps.Add(new JsonObject
            {
                ["id"] = d.DepId,
                ["name"] = d.DepName,
                ["publisher"] = d.DepPublisher,
                ["version"] = d.DepVersion,
            });
        }
        node["dependencies"] = deps;
        return SerializeIndented(node);
    }

    private static string BuildStandaloneAppJson(RuntimeTemplate template, StandaloneExtensionPlan plan)
    {
        var node = BaseAppJson(template);
        node["id"] = Guid.NewGuid().ToString();
        node["name"] = plan.ExtensionName;
        node["publisher"] = string.IsNullOrWhiteSpace(plan.Publisher) ? template.Defaults.Publisher : plan.Publisher;
        node["brief"] = plan.Brief;
        node["description"] = plan.Description;
        node["version"] = "0.0.0.1";
        node["application"] = plan.ApplicationVersion;
        node["platform"] = template.DefaultPlatform;
        node["runtime"] = plan.RuntimeVersion;
        node["idRanges"] = new JsonArray(new JsonObject
        {
            ["from"] = plan.IdRangeFrom,
            ["to"] = plan.IdRangeTo,
        });

        var deps = new JsonArray();
        foreach (var d in plan.Dependencies)
        {
            deps.Add(new JsonObject
            {
                ["id"] = d.DepId,
                ["name"] = d.DepName,
                ["publisher"] = d.DepPublisher,
                ["version"] = d.DepVersion,
            });
        }
        node["dependencies"] = deps;
        return SerializeIndented(node);
    }

    /// <summary>
    /// Re-serialises the template's stored defaults into a fresh
    /// <see cref="JsonObject"/>. Per-extension fields are layered on top by the
    /// callers above so the template-defined keys (target, features, etc.) are
    /// preserved verbatim.
    /// </summary>
    private static JsonObject BaseAppJson(RuntimeTemplate template)
    {
        var node = new JsonObject
        {
            ["id"] = "00000000-0000-0000-0000-000000000000",
            ["name"] = string.Empty,
        };
        var defaults = JsonNode.Parse(JsonSerializer.Serialize(template.Defaults, JsonOptions))?.AsObject();
        if (defaults is not null)
        {
            foreach (var kvp in defaults.ToList())
            {
                node[kvp.Key] = kvp.Value?.DeepClone();
            }
        }
        return node;
    }

    /// <summary>
    /// Returns the ForNAV catalogue entries to inject into Core's
    /// <c>dependencies</c> array when the user opts in. Uses the well-known
    /// catalogue rather than hardcoded GUIDs so admins control the list.
    /// </summary>
    private async Task<List<JsonObject>> LoadForNavDependenciesAsync(CancellationToken ct)
    {
        var rows = await _db.WellKnownDependencies
            .AsNoTracking()
            .Where(w => w.DepPublisher == ForNavPublisher)
            .OrderBy(w => w.Ordering)
            .ToListAsync(ct);
        return rows.Select(r => new JsonObject
        {
            ["id"] = r.DepId,
            ["name"] = r.DepName,
            ["publisher"] = r.DepPublisher,
            ["version"] = r.DepVersionDefault,
        }).ToList();
    }

    // ===== Code-workspace + README =====

    /// <summary>
    /// Serialises a <c>.code-workspace</c> JSON document with the given list
    /// of folder paths (workspace-relative, in display order) plus the fixed
    /// editor/AL settings. Used by both the workspace flow (Core + modules)
    /// and the sibling-extension flow (Core + every existing module + the
    /// new extension).
    /// </summary>
    private static string BuildCodeWorkspace(IReadOnlyList<string> folderPaths)
    {
        var folders = new JsonArray();
        foreach (var path in folderPaths)
        {
            folders.Add(new JsonObject { ["path"] = path });
        }

        var settings = JsonNode.Parse(WorkspaceSettingsJson)!.AsObject();
        var root = new JsonObject
        {
            ["folders"] = folders,
            ["settings"] = settings,
        };
        return SerializeIndented(root);
    }

    /// <summary>
    /// Serialise a <see cref="JsonNode"/> to indented JSON. Goes through
    /// <see cref="JsonSerializer"/> rather than <c>JsonNode.ToJsonString</c>
    /// because the latter has historically dropped the writer's indent setting
    /// — generation-engine.md mandates 2-space-indented output.
    /// </summary>
    private static string SerializeIndented(JsonNode node) =>
        JsonSerializer.Serialize(node, JsonOptions);

    private static string BuildReadme(ProjectPlan plan) =>
        $"""
        # {plan.WorkspaceName}

        {plan.Description}

        Generated by AL Dev Toolbox.
        """;

    // ===== Workspace extension identities =====

    /// <summary>
    /// Snapshots Core + every module assignment as
    /// <see cref="WorkspaceExtensionIdentity"/> rows for the workspace's
    /// <c>workspace.aldt.toml</c>. The GUID, id-range, and folder name baked
    /// in here are the same ones <see cref="ZipArchive"/> just stamped into
    /// the per-extension <c>app.json</c> files; persisting them lets a
    /// sibling-extension import quote real values back without guessing.
    /// </summary>
    private static IReadOnlyList<WorkspaceExtensionIdentity> BuildExtensionIdentities(
        RuntimeTemplate template,
        ProjectPlan plan,
        Guid coreId,
        string coreName,
        IReadOnlyList<ModuleAssignment> moduleInfos)
    {
        var rows = new List<WorkspaceExtensionIdentity>(moduleInfos.Count + 1)
        {
            new WorkspaceExtensionIdentity(
                Kind: WorkspaceExtensionIdentity.CoreKind,
                Key: null,
                Id: coreId,
                Name: coreName,
                Folder: "Core",
                Publisher: template.Defaults.Publisher,
                IdRangeFrom: plan.CoreIdRangeFrom,
                IdRangeTo: plan.CoreIdRangeTo),
        };
        foreach (var info in moduleInfos)
        {
            rows.Add(new WorkspaceExtensionIdentity(
                Kind: WorkspaceExtensionIdentity.ModuleKind,
                Key: info.Module.Key,
                Id: info.ExtensionId,
                Name: info.ExtensionName,
                Folder: info.FolderName,
                Publisher: template.Defaults.Publisher,
                IdRangeFrom: info.IdRangeFrom,
                IdRangeTo: info.IdRangeTo));
        }
        return rows;
    }

    // ===== ID range allocation =====

    /// <summary>
    /// Walks the selected modules in order, computing each one's contiguous
    /// id-range slice. Honours per-module size overrides while keeping the
    /// running offset correct.
    /// </summary>
    private static List<ModuleAssignment> AssignModuleRanges(
        RuntimeTemplate template,
        IReadOnlyList<Module> modules,
        ProjectPlan plan,
        Guid coreId,
        string coreName)
    {
        var assignments = new List<ModuleAssignment>(modules.Count);
        var nextStart = template.ModuleIdRangeStart;
        foreach (var module in modules)
        {
            var size = module.IdRangeSize ?? template.ModuleIdRangeSize;
            var rangeFrom = nextStart;
            var rangeTo = rangeFrom + size - 1;
            nextStart = rangeTo + 1;

            assignments.Add(new ModuleAssignment(
                Module: module,
                FolderName: StripWhitespace(module.Name),
                ExtensionName: $"{plan.WorkspaceName} {module.Name}",
                ExtensionId: Guid.NewGuid(),
                IdRangeFrom: rangeFrom,
                IdRangeTo: rangeTo,
                CoreId: coreId,
                CoreName: coreName));
        }
        return assignments;
    }

    private async Task<List<Module>> LoadSelectedModulesAsync(IReadOnlyList<string> moduleKeys, CancellationToken ct)
    {
        if (moduleKeys.Count == 0) return new();

        var rows = await _db.Modules
            .AsNoTracking()
            .Where(m => m.DeletedAt == null && moduleKeys.Contains(m.Key))
            .Include(m => m.Dependencies.OrderBy(d => d.Ordering))
            .ToListAsync(ct);

        // Preserve the user-selected ordering, since EF won't.
        var byKey = rows.ToDictionary(m => m.Key);
        var ordered = new List<Module>(moduleKeys.Count);
        foreach (var key in moduleKeys)
        {
            if (byKey.TryGetValue(key, out var module))
                ordered.Add(module);
        }
        return ordered;
    }

    // ===== Mustache substitution =====

    private string SubstituteMustache(string source, MustacheContext ctx)
    {
        return MustacheRegex.Replace(source, match =>
        {
            var key = match.Groups[1].Value;
            return key switch
            {
                "name" => ctx.Name,
                "workspaceName" => ctx.WorkspaceName,
                "shortName" => ctx.ShortName,
                "moduleName" => ctx.ModuleName,
                "publisher" => ctx.Publisher,
                "prefix" => ctx.Prefix,
                "namespace" => ctx.FolderPath.Replace('/', '.'),
                "guid" => Guid.NewGuid().ToString(),
                _ => UnknownVariable(match.Value, key),
            };
        });
    }

    private string UnknownVariable(string original, string key)
    {
        _logger.LogWarning("Unknown mustache variable {{{{{Key}}}}} encountered during generation; left as-is.", key);
        return original;
    }

    private record MustacheContext(
        string Name,
        string WorkspaceName,
        string ShortName,
        string ModuleName,
        string Publisher,
        string Prefix,
        string FolderPath);

    /// <summary>
    /// Builds a synthetic <see cref="ProjectPlan"/> shape so the standalone
    /// flow can reuse <see cref="WriteExtensionAsync"/> (which keys mustache
    /// variables off the workspace plan). Only the fields touched during
    /// substitution are filled in.
    /// </summary>
    private static ProjectPlan PlanForStandaloneSubstitution(StandaloneExtensionPlan plan) => new(
        TemplateKey: plan.TemplateKey,
        WorkspaceName: plan.ExtensionName,
        Brief: plan.Brief,
        Description: plan.Description,
        ApplicationVersion: plan.ApplicationVersion,
        RuntimeVersion: plan.RuntimeVersion,
        CoreIdRangeFrom: plan.IdRangeFrom,
        CoreIdRangeTo: plan.IdRangeTo,
        IncludeExamples: plan.IncludeExamples,
        IncludeForNav: false,
        SelectedModuleKeys: Array.Empty<string>());

    // ===== Validation =====

    private static void ValidateWorkspacePlan(ProjectPlan plan)
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(plan.TemplateKey)) errors[nameof(plan.TemplateKey)] = "Required.";
        if (string.IsNullOrWhiteSpace(plan.WorkspaceName) || !WorkspaceNameRegex.IsMatch(plan.WorkspaceName))
            errors[nameof(plan.WorkspaceName)] = "Required. Letters, digits and spaces only; must start with a letter.";
        if (plan.CoreIdRangeFrom <= 0) errors[nameof(plan.CoreIdRangeFrom)] = "Must be greater than zero.";
        if (plan.CoreIdRangeTo <= plan.CoreIdRangeFrom) errors[nameof(plan.CoreIdRangeTo)] = "Must be greater than 'from'.";
        // Milestone P2.4: ApplicationVersion + RuntimeVersion are now sourced
        // from the curated application_versions catalogue, so the regex checks
        // moved into the catalogue service. The plan still requires both to be
        // present — orphan templates without a curated row keep posting their
        // raw default_application / runtime strings.
        if (string.IsNullOrWhiteSpace(plan.ApplicationVersion))
            errors[nameof(plan.ApplicationVersion)] = "Required.";
        if (string.IsNullOrWhiteSpace(plan.RuntimeVersion))
            errors[nameof(plan.RuntimeVersion)] = "Required.";
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
        if (string.IsNullOrWhiteSpace(plan.ApplicationVersion))
            errors[nameof(plan.ApplicationVersion)] = "Required.";
        if (string.IsNullOrWhiteSpace(plan.RuntimeVersion))
            errors[nameof(plan.RuntimeVersion)] = "Required.";

        // The picker UI prevents duplicate selections, but a direct POST could
        // ship two rows with the same GUID — emit Each AL extension can only
        // declare a given dependency once, mirroring ModuleService.
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

    private static string StripWhitespace(string value) => Regex.Replace(value ?? string.Empty, @"\s+", string.Empty);

    /// <summary>Per-module derived data used during workspace generation.</summary>
    private record ModuleAssignment(
        Module Module,
        string FolderName,
        string ExtensionName,
        Guid ExtensionId,
        int IdRangeFrom,
        int IdRangeTo,
        Guid CoreId,
        string CoreName);
}

/// <summary>
/// Container for a finished archive. The stream is rewound and ready to copy
/// into the HTTP response body.
/// </summary>
public record GeneratedArchive(MemoryStream Stream, string FileName);

/// <summary>
/// Sibling-extension context: tells <see cref="GenerationService.GenerateExtensionAsync"/>
/// the new extension is being generated for an existing workspace. Drives the
/// extra <c>&lt;WorkspaceName&gt;.code-workspace</c> emitted at archive root
/// alongside the new extension folder.
/// <see cref="ExistingFolders"/> is the authoritative list of workspace
/// folders (Core + every existing extension) when the imported workspace
/// config carried persisted identities; the regenerated workspace file uses
/// these names verbatim so a module rename in the DB since the original
/// generation can't desync the user's repo. With an empty list (older
/// configs) we fall back to DB lookups via <see cref="ModuleKeys"/>.
/// </summary>
public record SiblingWorkspaceContext(
    string WorkspaceName,
    IReadOnlyList<string> ModuleKeys,
    IReadOnlyList<string> ExistingFolders);
