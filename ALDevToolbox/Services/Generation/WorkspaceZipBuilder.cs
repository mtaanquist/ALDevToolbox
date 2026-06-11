using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
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
        var shortName = GenerationNaming.StripWhitespace(plan.WorkspaceName);
        var rootFolder = shortName;
        var stream = new MemoryStream();
        var fileCount = 0;

        // {{publisher}} resolves to the org's configuration default, falling
        // back to the template default for a fresh org whose settings row is
        // still blank. Matches the per-extension publisher carried on each
        // EmittableExtension (see GenerationService) and the standalone flow.
        var publisher = GenerationNaming.ResolvePublisher(
            orgConfig.Settings.DefaultPublisher, template.Defaults.Publisher);

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Workspace-level assets: org logo + the always-included files the
            // template opts into. The .gitignore, the company ruleset, and
            // the README stub used to ship from embedded resources but moved
            // onto OrganizationFile rows so admins can curate them per
            // template — they come through WriteOrgFiles below.
            fileCount += WriteOrgLogo(archive, rootFolder, orgConfig.Logo, orgConfig.Settings.DefaultLogo);
            // Filter the org's file library against the template's opt-in
            // join once. WriteOrgFiles writes the workspace-root subset; the
            // per-extension subset gets written inside each extension folder
            // by WriteExtension below.
            var includedFiles = FilterIncluded(orgConfig.Files, template);
            fileCount += WriteOrgFiles(archive, rootFolder, includedFiles, plan, template, publisher);

            // Per-extension folders.
            foreach (var ext in extensions)
            {
                ct.ThrowIfCancellationRequested();
                fileCount += WriteExtension(archive, rootFolder, ext, extensions, template, plan, orgConfig, includedFiles);
            }

            var folderNames = extensions.Select(e => e.Path).ToList();
            var workspaceJsonCtx = new MustacheContext(
                Name: plan.WorkspaceName,
                WorkspaceName: plan.WorkspaceName,
                ShortName: shortName,
                ModuleName: plan.WorkspaceName,
                // Same resolved publisher the always-included files and each
                // extension's app.json use (org default, template fallback).
                Publisher: publisher,
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
        OrganizationConfig orgConfig,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var folderName = GenerationNaming.StripWhitespace(plan.ExtensionName);
        var stream = new MemoryStream();
        var fileCount = 0;

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Synthesise a one-extension Emittable + plan so the standalone
            // path reuses the same per-extension org-file + folder-tree
            // emission as the workspace path. app.json now arrives through
            // the per-extension org-file pipeline (seeded into every org).
            // Literal dependencies on the plan map directly to the
            // Emittable's dep list — no module / extension cross-refs
            // because there's only one extension here.
            var standaloneDeps = plan.Dependencies
                .Select(d => new EmittableDependency(
                    RefExtensionPath: null,
                    RefModuleKey: null,
                    LitId: d.DepId,
                    LitName: d.DepName,
                    LitPublisher: d.DepPublisher,
                    LitVersion: d.DepVersion))
                .ToList();
            var standaloneExt = new EmittableExtension(
                Path: folderName,
                Name: plan.ExtensionName,
                Id: Guid.NewGuid(),
                IdRangeFrom: plan.IdRangeFrom,
                IdRangeTo: plan.IdRangeTo,
                Application: plan.ApplicationVersion,
                Runtime: plan.RuntimeVersion,
                Publisher: plan.Publisher,
                IsModuleClone: false,
                ModuleKey: null,
                ModuleName: plan.ExtensionName,
                FolderRoots: scaffoldFolderRoots,
                Dependencies: standaloneDeps);
            var standaloneAsWorkspacePlan = new ProjectPlan(
                TemplateKey: plan.TemplateKey,
                WorkspaceName: plan.ExtensionName,
                ExtensionPrefix: string.Empty,
                Brief: plan.Brief,
                Description: plan.Description,
                ApplicationVersion: plan.ApplicationVersion,
                RuntimeVersion: plan.RuntimeVersion,
                CoreIdRangeFrom: plan.IdRangeFrom,
                CoreIdRangeTo: plan.IdRangeTo,
                IncludeExamples: plan.IncludeExamples,
                SelectedExtensionPaths: Array.Empty<string>(),
                SelectedModuleKeys: Array.Empty<string>(),
                TenantId: string.Empty);
            var allExtensions = new[] { standaloneExt };
            fileCount += WritePerExtensionOrgFiles(
                archive, folderName,
                FilterIncluded(orgConfig.Files, template),
                standaloneExt, allExtensions, template, standaloneAsWorkspacePlan, orgConfig);

            var substitutionCtx = BuildExtensionMustacheContext(standaloneExt, allExtensions, template, standaloneAsWorkspacePlan, orgConfig);
            fileCount += EmitFolderTree(archive, folderName, scaffoldFolderRoots, plan.IncludeExamples, substitutionCtx);

            WriteString(archive, $"{folderName}/{WorkspaceConfigService.FileName}", _config.BuildExtension(plan));
            fileCount++;

            if (sibling is not null)
            {
                var workspaceFile = $"{GenerationNaming.StripWhitespace(sibling.WorkspaceName)}.code-workspace";
                var existing = sibling.ExistingFolders.ToList();
                existing.Add(folderName);

                // Rewriting the sibling workspace's .code-workspace file: pull
                // the admin's JSON template from the org config so the result
                // matches what the workspace was originally generated with.
                var siblingShort = GenerationNaming.StripWhitespace(sibling.WorkspaceName);
                var siblingCtx = new MustacheContext(
                    Name: sibling.WorkspaceName,
                    WorkspaceName: sibling.WorkspaceName,
                    ShortName: siblingShort,
                    ModuleName: sibling.WorkspaceName,
                    // Matches the standalone extension's resolved publisher
                    // (org default, template fallback) — keep the rewritten
                    // sibling workspace in step with how it was generated.
                    Publisher: GenerationNaming.ResolvePublisher(
                        orgConfig.Settings.DefaultPublisher, template.Defaults.Publisher),
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
        return (stream, fileCount, folderName);
    }

    // ===== Per-extension emission =====

    private int WriteExtension(
        ZipArchive archive,
        string rootFolder,
        EmittableExtension ext,
        IReadOnlyList<EmittableExtension> allExtensions,
        RuntimeTemplate template,
        ProjectPlan plan,
        OrganizationConfig orgConfig,
        IReadOnlyList<OrganizationFile> includedFiles)
    {
        var extPath = $"{rootFolder}/{ext.Path}";
        var fileCount = 0;

        // Per-extension org files now include the canonical app.json (seeded
        // into every org via PlatformOrganizationFiles, with Scope =
        // EveryExtension and a mustache template body). The substitution
        // resolves the per-extension app.json inputs through the renderer
        // context built below.
        fileCount += WritePerExtensionOrgFiles(archive, extPath, includedFiles, ext, allExtensions, template, plan, orgConfig);

        var substitutionCtx = BuildExtensionMustacheContext(ext, allExtensions, template, plan, orgConfig);

        fileCount += EmitFolderTree(archive, extPath, ext.FolderRoots, plan.IncludeExamples, substitutionCtx);
        return fileCount;
    }

    /// <summary>
    /// Builds the per-extension <see cref="MustacheContext"/> with the full
    /// app.json variable set populated. The renderer reads from this context
    /// when an admin-edited <c>app.json</c> (or any other per-extension org
    /// file) references variables like <c>{{application_version}}</c> or
    /// <c>{{dependencies_array}}</c>.
    /// </summary>
    private MustacheContext BuildExtensionMustacheContext(
        EmittableExtension ext,
        IReadOnlyList<EmittableExtension> allExtensions,
        RuntimeTemplate template,
        ProjectPlan plan,
        OrganizationConfig orgConfig)
    {
        var deps = ResolveDependencies(ext, allExtensions);
        var idRanges = new JsonArray(new JsonObject
        {
            ["from"] = ext.IdRangeFrom,
            ["to"] = ext.IdRangeTo,
        });
        return new MustacheContext(
            Name: ext.Name,
            WorkspaceName: plan.WorkspaceName,
            ShortName: GenerationNaming.StripWhitespace(plan.WorkspaceName),
            ModuleName: ext.ModuleName,
            Publisher: ext.Publisher,
            ExtensionPrefix: plan.ExtensionPrefix,
            Affix: template.Defaults.AffixType == AffixType.None ? string.Empty : template.Defaults.Affix,
            FolderPath: string.Empty,
            TenantId: plan.TenantId,
            ExtensionId: ext.Id.ToString(),
            ExtensionName: ext.Name,
            Brief: plan.Brief,
            Description: plan.Description,
            Url: orgConfig.Settings.DefaultUrl ?? template.Defaults.Url ?? string.Empty,
            LogoPath: ResolveLogoPathForApp(orgConfig),
            PlatformVersion: template.Defaults.Platform,
            ApplicationVersion: ext.Application,
            Runtime: ext.Runtime,
            DependenciesArrayJson: deps.ToJsonString(CompactJsonOptions),
            IdRangesArrayJson: idRanges.ToJsonString(CompactJsonOptions));
    }

    /// <summary>
    /// Returns the path admin app.json templates should embed for the org
    /// logo. Mirrors the legacy hard-coded behaviour: the
    /// <see cref="OrganizationSettings.DefaultLogo"/> string when set,
    /// otherwise an empty string (no logo emitted, no logo referenced).
    /// </summary>
    private static string ResolveLogoPathForApp(OrganizationConfig orgConfig)
    {
        if (!string.IsNullOrWhiteSpace(orgConfig.Settings.DefaultLogo))
        {
            return orgConfig.Settings.DefaultLogo.Trim();
        }
        return string.Empty;
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

    // ===== dependency resolver (used by ExtensionMustacheContext) =====

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

    // ===== Org assets =====

    private static int WriteOrgLogo(ZipArchive archive, string rootFolder, OrganizationAsset? logo, string? defaultLogoPath)
    {
        if (logo is null) return 0;
        // app.json's `logo` field stores the path from an extension's
        // perspective, so it usually begins with `../` to escape Core/ back
        // to the workspace root. The ZIP entry must be workspace-relative;
        // strip any leading `../` segments to land at the same physical
        // location the app.json reference resolves to. If no DefaultLogo
        // has been set, fall back to the legacy `.assets/images/logo.{ext}`
        // layout so existing setups keep working.
        var ext = logo.ContentType switch
        {
            "image/svg+xml" => "svg",
            _ => "png",
        };
        var pathInsideZip = NormaliseLogoPath(defaultLogoPath, ext);
        var entry = archive.CreateEntry($"{rootFolder}/{pathInsideZip}", CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(logo.Content, 0, logo.Content.Length);
        return 1;
    }

    /// <summary>
    /// Resolves the workspace-relative emission path for the org logo from
    /// the admin's <see cref="OrganizationSettings.DefaultLogo"/>. Strips
    /// leading <c>./</c> and <c>../</c> segments (those are relative to an
    /// extension folder, not the workspace root). Empty / blank falls back
    /// to the legacy <c>.assets/images/logo.{ext}</c> path.
    /// </summary>
    internal static string NormaliseLogoPath(string? defaultLogoPath, string fileExtension)
    {
        if (string.IsNullOrWhiteSpace(defaultLogoPath))
        {
            return $".assets/images/logo.{fileExtension}";
        }
        var normalised = defaultLogoPath.Trim().Replace('\\', '/');
        while (normalised.StartsWith("../", StringComparison.Ordinal)) normalised = normalised[3..];
        while (normalised.StartsWith("./", StringComparison.Ordinal)) normalised = normalised[2..];
        return normalised.TrimStart('/');
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

    /// <summary>
    /// Emits the workspace-root-scoped files from <paramref name="files"/>.
    /// Per-extension-scoped rows are skipped here — see
    /// <see cref="WritePerExtensionOrgFiles"/> for the inside-each-extension
    /// counterpart. Splitting on scope keeps the mustache context coherent:
    /// workspace-root files build one context per workspace; per-extension
    /// files build one per extension so <c>{{name}}</c> /
    /// <c>{{extension_prefix}}</c> resolve to that extension's identity.
    /// </summary>
    private int WriteOrgFiles(
        ZipArchive archive,
        string rootFolder,
        IReadOnlyList<OrganizationFile> files,
        ProjectPlan plan,
        RuntimeTemplate template,
        string publisher)
    {
        if (files.Count == 0) return 0;
        var written = 0;
        var ctx = new MustacheContext(
            Name: plan.WorkspaceName,
            WorkspaceName: plan.WorkspaceName,
            ShortName: GenerationNaming.StripWhitespace(plan.WorkspaceName),
            ModuleName: plan.WorkspaceName,
            Publisher: publisher,
            ExtensionPrefix: plan.ExtensionPrefix,
            Affix: template.Defaults.AffixType == AffixType.None ? string.Empty : template.Defaults.Affix,
            FolderPath: string.Empty,
            TenantId: plan.TenantId);
        foreach (var file in files)
        {
            if (file.Scope != Domain.ValueObjects.OrganizationFileScope.WorkspaceRoot) continue;
            var content = file.MustacheEnabled
                ? _mustache.Render(file.Content, ctx with { FolderPath = file.Path })
                : file.Content;
            WriteString(archive, $"{rootFolder}/{file.Path}", content);
            written++;
        }
        return written;
    }

    /// <summary>
    /// Emits every per-extension-scoped row from <paramref name="files"/>
    /// into the supplied extension's folder. Mustache context is built off
    /// the extension's identity so <c>{{name}}</c> resolves to the
    /// per-extension rendered name (Core, Hotfix, the cloned module's
    /// extension_name). Replaces the old hard-coded AppSourceCop.json
    /// emission: admins author AppSourceCop.json as a normal
    /// OrganizationFile with scope = EveryExtension.
    /// </summary>
    private int WritePerExtensionOrgFiles(
        ZipArchive archive,
        string extensionFolderPath,
        IReadOnlyList<OrganizationFile> files,
        EmittableExtension ext,
        IReadOnlyList<EmittableExtension> allExtensions,
        RuntimeTemplate template,
        ProjectPlan plan,
        OrganizationConfig orgConfig)
    {
        if (files.Count == 0) return 0;
        var written = 0;
        // The full per-extension context — populated with app.json inputs —
        // so the canonical app.json template (and any other admin-authored
        // per-extension file) can reference variables like
        // {{application_version}} or {{dependencies_array}}. Content lands
        // verbatim (after substitution): admins authoring a JSON file own
        // its formatting, and inline `{{dependencies_array}}` interpolates
        // to a valid compact JSON array without needing a re-format pass.
        var ctx = BuildExtensionMustacheContext(ext, allExtensions, template, plan, orgConfig);
        foreach (var file in files)
        {
            if (file.Scope != Domain.ValueObjects.OrganizationFileScope.EveryExtension) continue;
            var content = file.MustacheEnabled
                ? _mustache.Render(file.Content, ctx with { FolderPath = file.Path })
                : file.Content;
            WriteString(archive, $"{extensionFolderPath}/{file.Path}", content);
            written++;
        }
        return written;
    }

    // ===== Workspace-level files =====

    /// <summary>
    /// Builds <c>{{short_name}}.code-workspace</c> by layering three sources
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
}
