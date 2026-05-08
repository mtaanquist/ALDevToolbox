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
    private static readonly Regex VersionRegex = new(@"^\d+\.\d+\.\d+\.\d+$", RegexOptions.Compiled);
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
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<GenerationService> _logger;

    public GenerationService(AppDbContext db, IWebHostEnvironment env, ILogger<GenerationService> logger)
    {
        _db = db;
        _env = env;
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

            // Core extension
            var coreAppJson = BuildCoreAppJson(template, plan, coreId, coreName);
            fileCount += await WriteExtensionAsync(
                archive,
                rootRelative: $"{rootFolder}/Core",
                appJson: coreAppJson,
                template: template,
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
                    plan: plan,
                    extensionName: info.ExtensionName,
                    moduleName: info.Module.Name,
                    ct: ct);
            }

            // Root files
            await WriteEmbeddedAsync(archive, $"{rootFolder}/.gitignore", "ALDevToolbox.Resources.al.gitignore", ct);
            WriteString(archive, $"{rootFolder}/{shortName}.code-workspace", BuildCodeWorkspace(moduleInfos));
            WriteString(archive, $"{rootFolder}/README.md", BuildReadme(plan));
            fileCount += 3;
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

    // ===== Standalone extension flow =====

    /// <summary>
    /// Generates a single-extension ZIP for the New Extension flow.
    /// </summary>
    public async Task<GeneratedArchive> GenerateExtensionAsync(StandaloneExtensionPlan plan, CancellationToken ct = default)
    {
        ValidateExtensionPlan(plan);

        var stopwatch = Stopwatch.StartNew();
        var template = await _db.RuntimeTemplates
            .AsNoTracking()
            .Include(t => t.Folders.OrderBy(f => f.Ordering))
            .Where(t => t.DeletedAt == null && t.Key == plan.TemplateKey)
            .FirstOrDefaultAsync(ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                [nameof(plan.TemplateKey)] = $"Template '{plan.TemplateKey}' was not found.",
            });

        var stream = new MemoryStream();
        var fileCount = 0;
        var folderName = StripWhitespace(plan.ExtensionName);

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var appJson = BuildStandaloneAppJson(template, plan);
            fileCount += await WriteExtensionAsync(
                archive,
                rootRelative: folderName,
                appJson: appJson,
                template: template,
                plan: PlanForStandaloneSubstitution(plan),
                extensionName: plan.ExtensionName,
                moduleName: plan.ExtensionName,
                publisherOverride: plan.Publisher,
                ct: ct);
        }

        stream.Position = 0;
        stopwatch.Stop();

        _logger.LogInformation(
            "Generated standalone extension '{Extension}' from template '{Template}' with {Deps} deps: {Files} files, {Bytes} bytes, {Ms} ms.",
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
    /// </summary>
    private async Task<int> WriteExtensionAsync(
        ZipArchive archive,
        string rootRelative,
        string appJson,
        RuntimeTemplate template,
        ProjectPlan plan,
        string extensionName,
        string moduleName,
        string? publisherOverride = null,
        CancellationToken ct = default)
    {
        WriteString(archive, $"{rootRelative}/app.json", appJson);
        WriteString(archive, $"{rootRelative}/AppSourceCop.json", JsonSerializer.Serialize(template.AppSourceCop, JsonOptions));
        WriteEmptyFile(archive, $"{rootRelative}/libs/.gitkeep");
        WriteEmptyFile(archive, $"{rootRelative}/permissionsets/.gitkeep");
        WriteEmptyFile(archive, $"{rootRelative}/Translations/.gitkeep");
        var fileCount = 5;

        var examplesRoot = ResolveExamplesRoot(template.Key);

        foreach (var folder in template.Folders.OrderBy(f => f.Ordering))
        {
            var folderRelative = $"{rootRelative}/{folder.Path}";
            var copiedAny = false;

            if (plan.IncludeExamples && !string.IsNullOrWhiteSpace(folder.ExamplePath) && examplesRoot is not null)
            {
                var examplesDir = Path.Combine(examplesRoot, folder.ExamplePath);
                if (Directory.Exists(examplesDir))
                {
                    foreach (var sourceFile in Directory.EnumerateFiles(examplesDir, "*", SearchOption.AllDirectories))
                    {
                        var relativeWithinExample = Path.GetRelativePath(examplesDir, sourceFile)
                            .Replace(Path.DirectorySeparatorChar, '/');
                        var destInZip = $"{folderRelative}/{relativeWithinExample}";

                        var raw = await File.ReadAllBytesAsync(sourceFile, ct);
                        if (sourceFile.EndsWith(".al", StringComparison.OrdinalIgnoreCase))
                        {
                            var text = Encoding.UTF8.GetString(raw);
                            var substituted = SubstituteMustache(text, new MustacheContext(
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
                            WriteBytes(archive, destInZip, raw);
                        }
                        fileCount++;
                        copiedAny = true;
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "Example folder missing for {Template}/{Path}: expected at {Dir}. Falling back to .gitkeep.",
                        template.Key, folder.Path, examplesDir);
                }
            }

            if (!copiedAny)
            {
                WriteEmptyFile(archive, $"{folderRelative}/.gitkeep");
                fileCount++;
            }
        }

        return fileCount;
    }

    // ===== app.json builders =====

    private string BuildCoreAppJson(RuntimeTemplate template, ProjectPlan plan, Guid coreId, string coreName)
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
            foreach (var d in BuildForNavDependencies()) coreDeps.Add(d);
        }
        node["dependencies"] = coreDeps;
        return node.ToJsonString(JsonOptions);
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
        return node.ToJsonString(JsonOptions);
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
        return node.ToJsonString(JsonOptions);
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
    private IEnumerable<JsonObject> BuildForNavDependencies()
    {
        var rows = _db.WellKnownDependencies
            .AsNoTracking()
            .Where(w => w.DepPublisher == ForNavPublisher)
            .OrderBy(w => w.Ordering)
            .ToList();
        foreach (var r in rows)
        {
            yield return new JsonObject
            {
                ["id"] = r.DepId,
                ["name"] = r.DepName,
                ["publisher"] = r.DepPublisher,
                ["version"] = r.DepVersionDefault,
            };
        }
    }

    // ===== Code-workspace + README =====

    private static string BuildCodeWorkspace(IReadOnlyList<ModuleAssignment> modules)
    {
        var folders = new JsonArray { new JsonObject { ["path"] = "Core" } };
        foreach (var m in modules)
        {
            folders.Add(new JsonObject { ["path"] = m.FolderName });
        }

        var settings = JsonNode.Parse(WorkspaceSettingsJson)!.AsObject();
        var root = new JsonObject
        {
            ["folders"] = folders,
            ["settings"] = settings,
        };
        return root.ToJsonString(JsonOptions);
    }

    private static string BuildReadme(ProjectPlan plan) =>
        $"""
        # {plan.WorkspaceName}

        {plan.Description}

        Generated by AL Dev Toolbox.
        """;

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
        if (string.IsNullOrWhiteSpace(plan.Brief)) errors[nameof(plan.Brief)] = "Required.";
        if (string.IsNullOrWhiteSpace(plan.Description)) errors[nameof(plan.Description)] = "Required.";
        if (plan.CoreIdRangeFrom <= 0) errors[nameof(plan.CoreIdRangeFrom)] = "Must be greater than zero.";
        if (plan.CoreIdRangeTo <= plan.CoreIdRangeFrom) errors[nameof(plan.CoreIdRangeTo)] = "Must be greater than 'from'.";
        if (string.IsNullOrWhiteSpace(plan.ApplicationVersion) || !VersionRegex.IsMatch(plan.ApplicationVersion))
            errors[nameof(plan.ApplicationVersion)] = "Must be a four-part version (e.g. 24.0.0.0).";
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
        if (string.IsNullOrWhiteSpace(plan.Brief)) errors[nameof(plan.Brief)] = "Required.";
        if (string.IsNullOrWhiteSpace(plan.Description)) errors[nameof(plan.Description)] = "Required.";
        if (string.IsNullOrWhiteSpace(plan.Publisher)) errors[nameof(plan.Publisher)] = "Required.";
        if (plan.IdRangeFrom <= 0) errors[nameof(plan.IdRangeFrom)] = "Must be greater than zero.";
        if (plan.IdRangeTo <= plan.IdRangeFrom) errors[nameof(plan.IdRangeTo)] = "Must be greater than 'from'.";
        if (string.IsNullOrWhiteSpace(plan.ApplicationVersion) || !VersionRegex.IsMatch(plan.ApplicationVersion))
            errors[nameof(plan.ApplicationVersion)] = "Must be a four-part version (e.g. 24.0.0.0).";
        if (string.IsNullOrWhiteSpace(plan.RuntimeVersion))
            errors[nameof(plan.RuntimeVersion)] = "Required.";
        if (errors.Count > 0) throw new PlanValidationException(errors);
    }

    // ===== ZIP helpers =====

    private static void WriteString(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void WriteBytes(ZipArchive archive, string path, byte[] bytes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
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

    // ===== Read-side helpers (used by the live preview) =====

    /// <summary>
    /// Returns the relative file paths (forward-slash, sorted) inside a single
    /// example folder. Used by the New Workspace live preview to show the same
    /// files the generator will emit when "Include examples" is on. Returns an
    /// empty list when the example folder is missing — the preview treats that
    /// the same as a <c>.gitkeep</c> placeholder.
    /// </summary>
    public IReadOnlyList<string> ListExampleFiles(string templateKey, string examplePath)
    {
        if (string.IsNullOrWhiteSpace(examplePath)) return Array.Empty<string>();
        var examplesRoot = ResolveExamplesRoot(templateKey);
        if (examplesRoot is null) return Array.Empty<string>();
        var examplesDir = Path.Combine(examplesRoot, examplePath);
        if (!Directory.Exists(examplesDir)) return Array.Empty<string>();

        return Directory.EnumerateFiles(examplesDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(examplesDir, f).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    // ===== Filesystem helpers =====

    /// <summary>
    /// Resolves the on-disk folder that holds a template's example AL files.
    /// Mirrors <c>SeedService</c>'s discovery: <c>SEED_PATH</c> first, otherwise
    /// the nearest <c>Templates.seed/</c> walking up from the content root.
    /// Returns <c>null</c> when nothing is found — callers fall back to
    /// <c>.gitkeep</c> placeholders.
    /// </summary>
    private string? ResolveExamplesRoot(string templateKey)
    {
        var seedPath = ResolveSeedPath();
        if (seedPath is null) return null;
        var examplesRoot = Path.Combine(seedPath, templateKey, "examples");
        return Directory.Exists(examplesRoot) ? examplesRoot : null;
    }

    private string? ResolveSeedPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable("SEED_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return Path.GetFullPath(fromEnv);

        var dir = new DirectoryInfo(_env.ContentRootPath);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Templates.seed");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
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
