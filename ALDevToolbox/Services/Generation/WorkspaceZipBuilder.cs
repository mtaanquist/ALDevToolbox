using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Services.Generation;

/// <summary>
/// Writes the in-memory ZIP for a workspace or standalone extension.
/// Pure: no DbContext, no <see cref="IOrganizationContext"/>; consumes the
/// resolved <see cref="EmittableExtension"/> list and <see cref="OrganizationConfig"/>
/// produced by <see cref="GenerationService"/>. Extracted from the monolithic
/// generation service in #86 so the ZIP-shape contract sits behind a single
/// API and can be unit-tested without standing up the full DI graph.
/// </summary>
public sealed class WorkspaceZipBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    private readonly MustacheRenderer _mustache;
    private readonly WorkspaceConfigService _config;

    public WorkspaceZipBuilder(MustacheRenderer mustache, WorkspaceConfigService config)
    {
        _mustache = mustache;
        _config = config;
    }

    /// <summary>
    /// Emits the full workspace ZIP. Returns the byte stream (rewound) and the
    /// file count for telemetry.
    /// </summary>
    internal async Task<(MemoryStream Stream, int FileCount)> BuildWorkspaceAsync(
        ProjectPlan plan,
        RuntimeTemplate template,
        IReadOnlyList<EmittableExtension> extensions,
        OrganizationConfig orgConfig,
        CancellationToken ct)
    {
        var shortName = StripWhitespace(plan.WorkspaceName);
        var rootFolder = shortName;
        var stream = new MemoryStream();
        var fileCount = 0;

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Workspace-level assets: org logo + the always-included files the
            // template opts into. The .gitignore, the company ruleset, and
            // the README stub used to ship from embedded resources but moved
            // onto OrganizationFile rows so admins can curate them per
            // template — they come through WriteOrgFiles below.
            fileCount += WriteOrgLogo(archive, $"{rootFolder}/.assets/images", orgConfig.Logo);
            fileCount += WriteOrgFiles(archive, rootFolder, FilterIncluded(orgConfig.Files, template), plan, template);

            // Per-extension folders.
            foreach (var ext in extensions)
            {
                fileCount += WriteExtension(archive, rootFolder, ext, extensions, template, plan);
            }

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
                FolderPath: string.Empty,
                TenantId: plan.TenantId);
            WriteString(archive, $"{rootFolder}/{shortName}.code-workspace",
                BuildCodeWorkspace(
                    orgConfig.Settings.CodeWorkspaceJson,
                    template.CodeWorkspaceJson,
                    folderNames,
                    workspaceJsonCtx));
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
            // Two emissions in this tail block: the .code-workspace file and
            // the workspace.aldt.toml side-car. The ruleset, .gitignore, and
            // README that used to sit here moved onto OrganizationFile rows.
            fileCount += 2;
        }

        stream.Position = 0;
        return (stream, fileCount);
    }

    /// <summary>
    /// Emits the standalone-extension ZIP. The orchestrator passes the
    /// scaffold's folder tree (typically the template's first required
    /// extension) and an optional sibling-workspace context for the case where
    /// the new extension is being added to an existing workspace.
    /// </summary>
    internal async Task<(MemoryStream Stream, int FileCount, string FolderName)> BuildStandaloneAsync(
        StandaloneExtensionPlan plan,
        RuntimeTemplate template,
        IReadOnlyList<FolderNode> scaffoldFolderRoots,
        SiblingWorkspaceContext? sibling,
        OrganizationConfig? siblingOrgConfig,
        CancellationToken ct)
    {
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
            fileCount += EmitFolderTree(archive, folderName, scaffoldFolderRoots, plan.IncludeExamples, substitutionCtx);

            WriteString(archive, $"{folderName}/{WorkspaceConfigService.FileName}", _config.BuildExtension(plan));
            fileCount++;

            if (sibling is not null && siblingOrgConfig is not null)
            {
                var workspaceFile = $"{StripWhitespace(sibling.WorkspaceName)}.code-workspace";
                var existing = sibling.ExistingFolders.Count > 0
                    ? sibling.ExistingFolders.ToList()
                    : new List<string>();
                existing.Add(folderName);

                // Rewriting the sibling workspace's .code-workspace file: pull
                // the admin's JSON template from the org config so the result
                // matches what the workspace was originally generated with.
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
                        siblingOrgConfig.Settings.CodeWorkspaceJson,
                        template.CodeWorkspaceJson,
                        existing,
                        siblingCtx));
                fileCount++;
            }
        }

        stream.Position = 0;
        return (stream, fileCount, folderName);
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
            FolderPath: string.Empty,
            TenantId: plan.TenantId);

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
                    ? _mustache.Render(file.Content, folderCtx)
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
    private static JsonArray ResolveDependencies(EmittableExtension ext, IReadOnlyList<EmittableExtension> allExtensions)
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

    /// <summary>
    /// Narrows <paramref name="files"/> to the ones the template has opted
    /// into via <see cref="RuntimeTemplate.IncludedFiles"/>. Ordering follows
    /// the join's <c>Ordering</c> column so admins control the emit sequence
    /// per template.
    /// </summary>
    private static IReadOnlyList<OrganizationFile> FilterIncluded(
        IReadOnlyList<OrganizationFile> files,
        RuntimeTemplate template)
    {
        if (template.IncludedFiles.Count == 0) return Array.Empty<OrganizationFile>();
        var byId = files.ToDictionary(f => f.Id);
        return template.IncludedFiles
            .OrderBy(j => j.Ordering)
            .Select(j => byId.TryGetValue(j.OrganizationFileId, out var f) ? f : null)
            .Where(f => f is not null)
            .Cast<OrganizationFile>()
            .ToList();
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
            FolderPath: string.Empty,
            TenantId: plan.TenantId);
        foreach (var file in files)
        {
            var content = file.MustacheEnabled
                ? _mustache.Render(file.Content, ctx with { FolderPath = file.Path })
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
        var substituted = _mustache.Render(source, ctx);
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

    private static string SerializeIndented(JsonNode node) =>
        JsonSerializer.Serialize(node, JsonOptions);

    private static string StripWhitespace(string value) =>
        Regex.Replace(value ?? string.Empty, @"\s+", string.Empty);
}
