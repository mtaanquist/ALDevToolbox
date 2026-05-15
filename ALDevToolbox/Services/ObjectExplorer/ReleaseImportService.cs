using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;
using OeModule = ALDevToolbox.Domain.Entities.ObjectExplorer.Module;
using OeModuleFile = ALDevToolbox.Domain.Entities.ObjectExplorer.ModuleFile;
using OeModuleObject = ALDevToolbox.Domain.Entities.ObjectExplorer.ModuleObject;
using OeModuleReference = ALDevToolbox.Domain.Entities.ObjectExplorer.ModuleReference;
using OeModuleSymbol = ALDevToolbox.Domain.Entities.ObjectExplorer.ModuleSymbol;
using OeModuleVariable = ALDevToolbox.Domain.Entities.ObjectExplorer.ModuleVariable;
using OeRelease = ALDevToolbox.Domain.Entities.ObjectExplorer.Release;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Ingests one Release worth of <c>.app</c> uploads into the
/// <c>oe_*</c> schema. Owns the Release lifecycle:
/// <c>ingesting → ready</c> on success, <c>ingesting → failed</c> on any
/// per-module exception. Each <c>.app</c> commits in its own SaveChanges
/// transaction so a 100-app DVD doesn't blow up the change tracker — but
/// the Release stays in <c>ingesting</c> until the final flip so partial
/// data is visibly partial.
///
/// See <c>.design/object-explorer.md</c> for the model and the resolution
/// strategy that reads back from the rows this service writes.
/// </summary>
public class ReleaseImportService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<ReleaseImportService> _logger;

    private static readonly HashSet<string> AllowedKinds = new(StringComparer.Ordinal)
    {
        "first_party",
        "third_party",
        "customer",
    };

    private static readonly Dictionary<string, string> TypeKeywordToObjectKind = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Codeunit"]  = "codeunit",
        ["Record"]    = "table",
        ["Page"]      = "page",
        ["Report"]    = "report",
        ["XmlPort"]   = "xmlport",
        ["Query"]     = "query",
        ["Interface"] = "interface",
        ["Enum"]      = "enum",
    };

    // Matches the opening line of an AL object declaration. The kind is in
    // group 1, the optional numeric id in 2, the unquoted name in 3 (or
    // bare-identifier name in 4 for ids-only kinds like interfaces). Compiled
    // once because we scan every .al file for every imported module.
    private static readonly Regex ObjectHeaderRegex = new(
        """^\s*(codeunit|table|page|report|xmlport|query|controladdin|enum|interface|permissionset|tableextension|pageextension|reportextension|enumextension|permissionsetextension)\s+(?:(\d+)\s+)?(?:"(?<quoted>[^"]+)"|(?<bare>\w+))""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ReleaseImportService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ILogger<ReleaseImportService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; ReleaseImportService called outside an authenticated request.");

    /// <summary>
    /// Creates a Release and ingests every supplied <c>.app</c> upload. Per-app
    /// failures abort the run and flip the Release to <c>failed</c>; partial
    /// modules stay in the DB so an operator can inspect what got through.
    /// </summary>
    public async Task<ReleaseImportSummary> ImportReleaseAsync(
        ReleaseImportRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var orgId = RequireOrganizationId();
        Validate(request);

        var release = new OeRelease
        {
            OrganizationId = orgId,
            Label = request.Label.Trim(),
            Kind = request.Kind,
            ParentReleaseId = request.ParentReleaseId,
            ApplicationVersionId = request.ApplicationVersionId,
            Status = "ingesting",
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.OeReleases.Add(release);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Started Release ingest: ReleaseId={ReleaseId} Label={Label} Kind={Kind} ParentReleaseId={ParentReleaseId} Uploads={UploadCount}",
            release.Id, release.Label, release.Kind, release.ParentReleaseId, request.Uploads.Count);

        var totals = new ImportTotals();
        try
        {
            foreach (var upload in request.Uploads)
            {
                ct.ThrowIfCancellationRequested();
                await ImportOneAppAsync(orgId, release, upload, totals, ct).ConfigureAwait(false);
            }

            // Pin the platform/application version from the canonical Base App
            // module when one came in this Release. Continue to leave null if
            // the upload was third-party-only. Re-find via the cleared tracker
            // because per-module SaveChanges has detached the entity already.
            _db.ChangeTracker.Clear();
            var ready = await _db.OeReleases.FindAsync(new object?[] { release.Id }, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Release {release.Id} disappeared mid-ingest.");
            ready.BcVersion = InferBcVersion(release.Id);
            ready.Status = "ready";
            ready.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Completed Release ingest: ReleaseId={ReleaseId} ModulesImported={ModulesImported} ModulesSkipped={ModulesSkipped} ObjectsImported={ObjectsImported} ReferencesImported={ReferencesImported}",
                release.Id, totals.ModulesImported, totals.ModulesSkipped, totals.ObjectsImported, totals.ReferencesImported);

            return new ReleaseImportSummary(
                ReleaseId: release.Id,
                ModulesImported: totals.ModulesImported,
                ModulesSkipped: totals.ModulesSkipped,
                ObjectsImported: totals.ObjectsImported,
                ReferencesImported: totals.ReferencesImported,
                SourceFilesImported: totals.SourceFilesImported);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Release ingest failed: ReleaseId={ReleaseId} ModulesImportedBeforeFailure={ModulesImported}",
                release.Id, totals.ModulesImported);
            // Stamp the release as failed so the UI can show the operator which
            // upload didn't make it. SaveChanges in a fresh tracker state so
            // we don't drag the failed entity's tracker into the status update.
            _db.ChangeTracker.Clear();
            var failed = await _db.OeReleases.FindAsync(new object?[] { release.Id }, ct).ConfigureAwait(false);
            if (failed is not null)
            {
                failed.Status = "failed";
                failed.StatusMessage = ex.Message;
                failed.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            throw;
        }
    }

    // ── Validation ──────────────────────────────────────────────────────

    private static void Validate(ReleaseImportRequest req)
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(req.Label))
        {
            errors["Label"] = "Label is required.";
        }
        if (!AllowedKinds.Contains(req.Kind))
        {
            errors["Kind"] = $"Kind must be one of: {string.Join(", ", AllowedKinds)}.";
        }
        if (req.Uploads.Count == 0)
        {
            errors["Uploads"] = "At least one .app file is required.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);
    }

    // ── Per-app ingest ──────────────────────────────────────────────────

    private async Task ImportOneAppAsync(
        int orgId, OeRelease release, AppFileUpload upload, ImportTotals totals, CancellationToken ct)
    {
        var pkg = await AppPackageReader.ReadAsync(upload.AppStream, ct).ConfigureAwait(false);

        // Idempotency: if this Release already has a module with the same
        // (AppId, Version) AND the same byte hash, treat the upload as a
        // silent no-op. Same (AppId, Version) with a *different* hash is a
        // genuine surprise — the AL ecosystem doesn't rebuild .apps with
        // identical (AppId, Version) and different bytes — so we surface it
        // as an error.
        var existing = await _db.OeModules
            .AsNoTracking()
            .Where(m => m.ReleaseId == release.Id && m.AppId == pkg.Manifest.AppId && m.Version == pkg.Manifest.Version)
            .Select(m => new { m.Id, m.AppFileHash })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);

        if (existing is not null)
        {
            if (string.Equals(existing.AppFileHash, pkg.AppFileHash, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Skipping byte-identical re-upload of {File} (AppId={AppId} Version={Version}) into ReleaseId={ReleaseId}",
                    upload.FileName, pkg.Manifest.AppId, pkg.Manifest.Version, release.Id);
                totals.ModulesSkipped++;
                return;
            }
            throw new PlanValidationException(new Dictionary<string, string>
            {
                [$"Uploads.{upload.FileName}"] =
                    $".app with AppId {pkg.Manifest.AppId} version {pkg.Manifest.Version} already exists in this Release with a different content hash. Start a new Release instead of overwriting.",
            });
        }

        // Merge in source from the paired .Source.zip when the .app didn't
        // embed source. Either way, sourceFiles ends up keyed by relative path.
        var sourceFiles = pkg.SourceFiles.ToDictionary(f => f.Path, f => f.Content, StringComparer.OrdinalIgnoreCase);
        if (upload.SourceZipStream is not null && pkg.SourceFiles.Count == 0)
        {
            foreach (var (path, content) in ReadSourceZip(upload.SourceZipStream))
            {
                sourceFiles[path] = content;
            }
        }

        await WriteModuleAsync(orgId, release, pkg, sourceFiles, totals, ct).ConfigureAwait(false);
    }

    private async Task WriteModuleAsync(
        int orgId, OeRelease release,
        AppPackage pkg,
        IReadOnlyDictionary<string, string> sourceFiles,
        ImportTotals totals,
        CancellationToken ct)
    {
        var module = new OeModule
        {
            OrganizationId = orgId,
            ReleaseId = release.Id,
            AppId = pkg.Manifest.AppId,
            Name = pkg.Manifest.Name,
            Publisher = pkg.Manifest.Publisher,
            Version = pkg.Manifest.Version,
            Target = pkg.Manifest.Target,
            Runtime = pkg.Manifest.Runtime,
            IsTest = false,         // determined by upload-folder rules (later PR)
            IsInternal = false,     // determined by the _Exclude_ filename marker (later PR)
            IsLanguagePack = false,
            DependenciesJson = SerializeDeps(pkg.Manifest.Dependencies),
            AppFileHash = pkg.AppFileHash,
            CreatedAt = DateTime.UtcNow,
        };
        _db.OeModules.Add(module);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Files first so we can resolve ModuleObject.SourceFileId on the way.
        // Symbol-package ReferenceSourceFileName is the full relative path
        // (e.g. "src/Codeunits/DKCoreEventSubscribers.Codeunit.al"), so we
        // key by full path. The parser already normalised both the embedded
        // src/ tree (.app) and the paired .Source.zip to the same "src/…" shape.
        var filesByPath = new Dictionary<string, OeModuleFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, content) in sourceFiles)
        {
            var file = new OeModuleFile
            {
                OrganizationId = orgId,
                ModuleId = module.Id,
                Path = path,
                Content = content,
                ContentHash = HashHex(content),
                LineCount = CountLines(content),
            };
            _db.OeModuleFiles.Add(file);
            filesByPath[path] = file;
            totals.SourceFilesImported++;
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Pre-scan each source file once for object declaration line numbers,
        // so we can stamp ModuleObject.LineNumber without re-reading content.
        var declarationLines = ScanDeclarationLines(filesByPath);

        foreach (var symObj in pkg.Symbols.Objects)
        {
            ct.ThrowIfCancellationRequested();

            OeModuleFile? sourceFile = null;
            int line = 1;
            if (!string.IsNullOrEmpty(symObj.ReferenceSourceFileName)
                && filesByPath.TryGetValue(symObj.ReferenceSourceFileName, out var matchedFile))
            {
                sourceFile = matchedFile;
                if (declarationLines.TryGetValue((matchedFile.Path, symObj.Kind, symObj.Name), out var found))
                {
                    line = found;
                }
            }

            var obj = new OeModuleObject
            {
                OrganizationId = orgId,
                ModuleId = module.Id,
                Kind = symObj.Kind,
                ObjectId = symObj.ObjectId,
                Name = symObj.Name,
                Namespace = string.IsNullOrEmpty(symObj.Namespace) ? null : symObj.Namespace,
                ExtendsAppId = symObj.ExtendsAppId,
                ExtendsObjectName = symObj.ExtendsObjectName,
                SourceFile = sourceFile,
                LineNumber = line,
            };
            _db.OeModuleObjects.Add(obj);
            totals.ObjectsImported++;

            EmitSymbols(orgId, module, obj, symObj);
            EmitVariables(orgId, module, obj, symObj);
            EmitReferences(orgId, module, obj, symObj, totals);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        totals.ModulesImported++;

        // Clear the tracker between modules so a release-wide import doesn't
        // turn into an O(n²) walk over an ever-growing change-tracker.
        _db.ChangeTracker.Clear();
    }

    private void EmitSymbols(int orgId, OeModule module, OeModuleObject obj, SymbolObject symObj)
    {
        foreach (var method in symObj.Methods)
        {
            _db.OeModuleSymbols.Add(new OeModuleSymbol
            {
                OrganizationId = orgId,
                ModuleId = module.Id,
                Object = obj,
                Kind = method.IsInternal ? "internal_procedure" : "procedure",
                Name = method.Name,
                Signature = RenderSignature(method),
                ReturnType = method.ReturnType?.Name,
                LineNumber = 0,    // Filled in by a source-scan pass later (PR 4 territory).
                ColumnStart = 0,
                ColumnEnd = 0,
            });
        }
        foreach (var field in symObj.Fields)
        {
            _db.OeModuleSymbols.Add(new OeModuleSymbol
            {
                OrganizationId = orgId,
                ModuleId = module.Id,
                Object = obj,
                Kind = "field",
                Name = field.Name,
                Signature = field.Type.Name,
                FieldId = field.Id,
                LineNumber = 0,
                ColumnStart = 0,
                ColumnEnd = 0,
            });
        }
    }

    private void EmitVariables(int orgId, OeModule module, OeModuleObject obj, SymbolObject symObj)
    {
        foreach (var variable in symObj.Variables)
        {
            var (targetKind, targetId, targetName, typeKeyword) = ResolveVariableTarget(variable.Type, module.AppId);
            _db.OeModuleVariables.Add(new OeModuleVariable
            {
                OrganizationId = orgId,
                ModuleId = module.Id,
                Object = obj,
                Name = variable.Name,
                TypeKeyword = typeKeyword,
                TypeName = variable.Type.ObjectName ?? variable.Type.Name,
                TargetAppId = variable.Type.ModuleId,
                TargetObjectKind = targetKind,
                TargetObjectId = targetId,
                TargetObjectName = targetName,
            });
        }
    }

    private void EmitReferences(int orgId, OeModule module, OeModuleObject obj, SymbolObject symObj, ImportTotals totals)
    {
        // 1. extends_target — for *extension kinds, the base object lives in
        //    another module (or, for same-publisher cases, this one).
        if (symObj.ExtendsAppId is not null && !string.IsNullOrEmpty(symObj.ExtendsObjectName))
        {
            var extendedKind = symObj.Kind switch
            {
                "tableextension"          => "table",
                "pageextension"           => "page",
                "reportextension"         => "report",
                "enumextension"           => "enum",
                "permissionsetextension"  => "permissionset",
                _ => null,
            };
            if (extendedKind is not null)
            {
                _db.OeModuleReferences.Add(new OeModuleReference
                {
                    OrganizationId = orgId,
                    ModuleId = module.Id,
                    SourceObject = obj,
                    TargetAppId = symObj.ExtendsAppId.Value,
                    TargetObjectKind = extendedKind,
                    TargetObjectName = symObj.ExtendsObjectName!,
                    ReferenceKind = "extends_target",
                });
                totals.ReferencesImported++;
            }
        }

        // 2. variable_type — one ref per AL-object-typed object-scoped variable.
        foreach (var variable in symObj.Variables)
        {
            var (kind, id, name, _) = ResolveVariableTarget(variable.Type, module.AppId);
            if (kind is null || name is null) continue;     // non-AL type or unresolved.
            var targetAppId = variable.Type.ModuleId ?? module.AppId;
            _db.OeModuleReferences.Add(new OeModuleReference
            {
                OrganizationId = orgId,
                ModuleId = module.Id,
                SourceObject = obj,
                TargetAppId = targetAppId,
                TargetObjectKind = kind,
                TargetObjectId = id,
                TargetObjectName = name,
                ReferenceKind = "variable_type",
            });
            totals.ReferencesImported++;
        }

        // 3. return_type — one ref per AL-object-typed procedure return.
        foreach (var method in symObj.Methods)
        {
            if (method.ReturnType is null) continue;
            var (kind, id, name, _) = ResolveVariableTarget(method.ReturnType, module.AppId);
            if (kind is null || name is null) continue;
            var targetAppId = method.ReturnType.ModuleId ?? module.AppId;
            _db.OeModuleReferences.Add(new OeModuleReference
            {
                OrganizationId = orgId,
                ModuleId = module.Id,
                SourceObject = obj,
                TargetAppId = targetAppId,
                TargetObjectKind = kind,
                TargetObjectId = id,
                TargetObjectName = name,
                ReferenceKind = "return_type",
            });
            totals.ReferencesImported++;
        }

        // 4. parameter_type — one ref per AL-object-typed parameter.
        foreach (var method in symObj.Methods)
        {
            foreach (var param in method.Parameters)
            {
                var (kind, id, name, _) = ResolveVariableTarget(param.Type, module.AppId);
                if (kind is null || name is null) continue;
                var targetAppId = param.Type.ModuleId ?? module.AppId;
                _db.OeModuleReferences.Add(new OeModuleReference
                {
                    OrganizationId = orgId,
                    ModuleId = module.Id,
                    SourceObject = obj,
                    TargetAppId = targetAppId,
                    TargetObjectKind = kind,
                    TargetObjectId = id,
                    TargetObjectName = name,
                    ReferenceKind = "parameter_type",
                });
                totals.ReferencesImported++;
            }
        }

        // 5. table_no — codeunit "TableNo" property. The value is a raw
        //    "#<32hex>#<name>" string just like ExtendsTarget.
        var tableNo = symObj.Properties.FirstOrDefault(p =>
            string.Equals(p.Name, "TableNo", StringComparison.OrdinalIgnoreCase));
        if (tableNo is not null)
        {
            var (appId, name) = ParseHashRef(tableNo.Value);
            if (appId is not null && name is not null)
            {
                _db.OeModuleReferences.Add(new OeModuleReference
                {
                    OrganizationId = orgId,
                    ModuleId = module.Id,
                    SourceObject = obj,
                    TargetAppId = appId.Value,
                    TargetObjectKind = "table",
                    TargetObjectName = name,
                    ReferenceKind = "table_no",
                });
                totals.ReferencesImported++;
            }
        }
    }

    // ── Resolution helpers ──────────────────────────────────────────────

    /// <summary>
    /// Maps a parser-side <see cref="SymbolTypeRef"/> into the
    /// <c>(kind, id, name, typeKeyword)</c> tuple the entity rows need.
    /// Returns nulls for non-AL types so callers can skip the reference row.
    /// </summary>
    private static (string? Kind, int? Id, string? Name, string? TypeKeyword) ResolveVariableTarget(SymbolTypeRef type, Guid importingAppId)
    {
        if (!TypeKeywordToObjectKind.TryGetValue(type.Name, out var kind))
        {
            return (null, null, null, null);
        }
        if (string.IsNullOrEmpty(type.ObjectName))
        {
            return (null, null, null, type.Name);
        }
        return (kind, type.ObjectId, type.ObjectName, type.Name);
    }

    private static (Guid? AppId, string? Name) ParseHashRef(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw[0] != '#') return (null, null);
        var second = raw.IndexOf('#', 1);
        if (second != 33) return (null, null);
        if (!Guid.TryParseExact(raw.AsSpan(1, 32), "N", out var guid)) return (null, null);
        return (guid, raw.Substring(34));
    }

    private static string RenderSignature(SymbolMethod method)
    {
        if (method.Parameters.Count == 0) return "()";
        var parts = method.Parameters.Select(p => $"{p.Name}: {p.Type.ObjectName ?? p.Type.Name}");
        return "(" + string.Join(", ", parts) + ")";
    }

    private static string SerializeDeps(IReadOnlyList<AppDependency> deps)
    {
        if (deps.Count == 0) return "[]";
        var parts = deps.Select(d => $"{{\"id\":\"{d.AppId}\",\"name\":{JsonString(d.Name)},\"publisher\":{JsonString(d.Publisher)},\"version\":{JsonString(d.Version)}}}");
        return "[" + string.Join(",", parts) + "]";
    }

    private static string JsonString(string s)
    {
        // Tight enough for our subset — manifests have well-behaved names,
        // and we'd rather not pull a full JsonSerializer call for a 4-field
        // record per dependency.
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string HashHex(string content)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash);
    }

    private static int CountLines(string content)
    {
        if (string.IsNullOrEmpty(content)) return 0;
        int n = 1;
        foreach (var c in content) if (c == '\n') n++;
        return n;
    }

    // ── Source-zip handling ─────────────────────────────────────────────

    private static IEnumerable<(string Path, string Content)> ReadSourceZip(Stream zipStream)
    {
        using var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (!entry.FullName.EndsWith(".al", StringComparison.OrdinalIgnoreCase)) continue;

            var path = entry.FullName.StartsWith("src/", StringComparison.OrdinalIgnoreCase)
                ? entry.FullName
                : "src/" + entry.FullName;

            using var s = entry.Open();
            using var reader = new StreamReader(s);
            yield return (path, reader.ReadToEnd());
        }
    }

    // ── Source declaration scan ─────────────────────────────────────────

    /// <summary>
    /// Walks every <c>.al</c> file once and records the 1-based line number of
    /// every <c>(kind, name)</c> object declaration. Cheap (one regex match
    /// per non-blank line); avoids a per-object file re-read for thousands of
    /// symbol-package objects.
    /// </summary>
    private static Dictionary<(string FilePath, string Kind, string Name), int> ScanDeclarationLines(
        IReadOnlyDictionary<string, OeModuleFile> filesByPath)
    {
        var result = new Dictionary<(string, string, string), int>();
        foreach (var (_, file) in filesByPath)
        {
            int line = 0;
            foreach (var rawLine in file.Content.Split('\n'))
            {
                line++;
                var m = ObjectHeaderRegex.Match(rawLine);
                if (!m.Success) continue;
                var kind = m.Groups[1].Value.ToLowerInvariant();
                var name = m.Groups["quoted"].Success ? m.Groups["quoted"].Value : m.Groups["bare"].Value;
                result.TryAdd((file.Path, kind, name), line);
            }
        }
        return result;
    }

    // ── BC version inference ────────────────────────────────────────────

    /// <summary>
    /// Picks a stable platform/application version label from the Modules just
    /// imported. Microsoft's Base Application carries the canonical version
    /// stamp; when it's present we use that. Otherwise leave null and let the
    /// admin set <see cref="ReleaseImportRequest.ApplicationVersionId"/> by
    /// hand on retry. Reads from the DB rather than tracker state because
    /// per-module SaveChanges has cleared the tracker by now.
    /// </summary>
    private string? InferBcVersion(int releaseId)
    {
        var baseApp = _db.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == releaseId
                && m.Publisher == "Microsoft"
                && (m.Name == "Base Application" || m.Name == "Application"))
            .Select(m => m.Version)
            .FirstOrDefault();
        return baseApp;
    }

    // ── Mutable tally ───────────────────────────────────────────────────

    private sealed class ImportTotals
    {
        public int ModulesImported;
        public int ModulesSkipped;
        public int ObjectsImported;
        public int ReferencesImported;
        public int SourceFilesImported;
    }
}
