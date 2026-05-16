using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Al;
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
        await EnsureLabelAvailableAsync(orgId, request.Label.Trim(), ct).ConfigureAwait(false);

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

            // Phase 2 call-site extraction. Runs once per release, AFTER
            // every module's symbols + variables are in the DB so the
            // resolver can see types declared anywhere in this release.
            // Cross-release receivers (DK Core file references Customer
            // from a parent Base App release) currently drop — phase 2.1
            // can add the chain walk when needed.
            await EmitCallSiteReferencesAsync(orgId, release.Id, totals, ct).ConfigureAwait(false);

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

    /// <summary>
    /// Refuses a label that's already in use by another active Release in the
    /// same org. The DB also enforces this via <c>ix_oe_releases_org_label_active</c>
    /// (partial unique index on <c>(organization_id, label)</c> filtered by
    /// <c>deleted_at IS NULL</c>) — the pre-check exists so admins get a clean
    /// field-keyed error instead of a raw Postgres 23505 surfacing past the
    /// failed-status update path. Soft-deleted labels remain reusable since
    /// the partial index excludes them.
    /// </summary>
    private async Task EnsureLabelAvailableAsync(int orgId, string label, CancellationToken ct)
    {
        var taken = await _db.OeReleases.AsNoTracking()
            .AnyAsync(r => r.OrganizationId == orgId && r.DeletedAt == null && r.Label == label, ct)
            .ConfigureAwait(false);
        if (taken)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Label"] = $"A Release labelled \"{label}\" already exists in this organisation. Soft-delete it from the admin page first, or pick a different label.",
            });
        }
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

        // Source priority: a paired .Source.zip wins over the .app's
        // embedded source whenever one was uploaded alongside the .app.
        // Microsoft's BC 28+ first-party modules ship as Ready2Run wrappers
        // whose inner .app's embedded source is partial — the canonical
        // full source tree sits in the sibling <Name>.Source.zip on the
        // DVD. Falling back to pkg.SourceFiles only when no .Source.zip
        // was provided keeps single-file partner uploads (which never pair
        // a zip) working as before.
        IReadOnlyDictionary<string, string> sourceFiles;
        if (upload.SourceZipStream is not null)
        {
            var fromZip = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, content) in ReadSourceZip(upload.SourceZipStream))
            {
                fromZip[path] = content;
            }
            sourceFiles = fromZip;
        }
        else
        {
            sourceFiles = pkg.SourceFiles.ToDictionary(f => f.Path, f => f.Content, StringComparer.OrdinalIgnoreCase);
        }

        await WriteModuleAsync(orgId, release, upload, pkg, sourceFiles, totals, ct).ConfigureAwait(false);
    }

    private async Task WriteModuleAsync(
        int orgId, OeRelease release,
        AppFileUpload upload,
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
            // Flags come from the upload-layer inference (folder names,
            // _Exclude_ marker, language-pack name pattern). The per-file
            // upload path leaves all three at false; the folder-ZIP path
            // sets them based on the DVD's folder conventions.
            IsTest = upload.IsTest,
            IsInternal = upload.IsInternal,
            IsLanguagePack = upload.IsLanguagePack,
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
        //
        // SaveChanges in chunks of FileChunkSize: Base App can carry several
        // thousand .al files with multi-KB Content each, and EF's batch
        // builder allocates the whole batch text + parameter array in
        // memory. Bounded chunks keep the per-flush memory footprint flat.
        var filesByPath = new Dictionary<string, OeModuleFile>(StringComparer.OrdinalIgnoreCase);
        int filesPending = 0;
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
            filesPending++;
            if (filesPending >= FileChunkSize)
            {
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                filesPending = 0;
            }
        }
        if (filesPending > 0)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // Build the (Kind, Name) → (File, Line) index used to link
        // symbol-package objects to their .al file. We deliberately do
        // *not* use ReferenceSourceFileName: it's path-string-based and
        // the .Source.zip layouts Microsoft ships are inconsistent
        // enough within a single release that no canonicaliser can
        // bridge the gap (System Application Test Library uses
        // "Password/src/LibraryPassword.Codeunit.al", Business Foundation
        // Test Libraries uses "NoSeries/src/LibraryNoSeries.Codeunit.al",
        // first-party Base App uses bare "Codeunits/Foo.Codeunit.al" —
        // all in BC 28.1 DK). The AL declaration at the top of each .al
        // file is deterministic; AL enforces one object per file in
        // practice; matching by that header is the stable contract.
        var declarations = ScanFileDeclarations(filesByPath);

        // Same pass, deeper: run the AL symbol extractor over each file so we
        // can stamp line/column on every sub-symbol (procedure / trigger /
        // event publisher/subscriber / field) and emit rows for ones the
        // symbol package doesn't ship (local procedures, triggers). Keyed by
        // file path so the per-object loop below can grab its file's symbols
        // in O(1). Files with no source — third-party modules built with
        // IncludeSourceInSymbolFile="false" and no paired .Source.zip — have
        // no entry here; sub-symbols for those objects stay at LineNumber=0.
        var extractedByPath = ExtractSubSymbolsByFile(filesByPath);

        // Each chunk holds the object + every symbol/variable/reference row
        // that references it via navigation. Saving them together lets EF
        // resolve the dependent FKs from the principal's freshly-generated
        // Id; the tracker clear between chunks drops the per-chunk memory
        // pressure so a 5000-object Base App doesn't grow unbounded.
        int objectsPending = 0;
        int objectsExpectingSource = 0;
        int objectsLinked = 0;
        foreach (var symObj in pkg.Symbols.Objects)
        {
            ct.ThrowIfCancellationRequested();

            OeModuleFile? sourceFile = null;
            int line = 1;
            // ReferenceSourceFileName drives the "should this object
            // have linked?" diagnostic counter only — the actual link
            // is established by matching the AL header in the .al
            // file, which is layout-agnostic.
            if (!string.IsNullOrEmpty(symObj.ReferenceSourceFileName))
            {
                objectsExpectingSource++;
            }
            if (declarations.TryGetValue((symObj.Kind, symObj.Name), out var hit))
            {
                sourceFile = hit.File;
                line = hit.Line;
                objectsLinked++;
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
                // Use the FK directly rather than the navigation: after the
                // file-chunk save loop above, the file entity may have been
                // detached from the tracker on a previous flush. The Id is
                // intact on the captured reference; bind by Id to side-step
                // any tracker-state assumption.
                SourceFileId = sourceFile?.Id,
                LineNumber = line,
            };
            _db.OeModuleObjects.Add(obj);
            totals.ObjectsImported++;

            // Only feed the extractor's symbols in when the file's primary
            // object_declaration matches this symObj — for multi-object .al
            // files the extractor only marks the first object, so anything
            // past that risks attributing the second object's procedures to
            // the first. Bound the over-attribution by being strict here.
            IReadOnlyList<AlSymbol> extractedForObject = Array.Empty<AlSymbol>();
            if (sourceFile is not null && extractedByPath.TryGetValue(sourceFile.Path, out var extracted))
            {
                var primaryDecl = extracted.FirstOrDefault(s => s.Kind == "object_declaration");
                if (primaryDecl is not null
                    && string.Equals(primaryDecl.Name, symObj.Name, StringComparison.OrdinalIgnoreCase))
                {
                    extractedForObject = extracted;
                }
            }

            EmitSymbols(orgId, module, obj, symObj, extractedForObject);
            EmitVariables(orgId, module, obj, symObj);
            EmitReferences(orgId, module, obj, symObj, totals);

            objectsPending++;
            if (objectsPending >= ObjectChunkSize)
            {
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                // Drop tracker state once the chunk is committed. The next
                // iteration's object will only navigate via SourceFileId
                // (already a primitive FK), so detaching the file entities
                // here is fine.
                _db.ChangeTracker.Clear();
                objectsPending = 0;
            }
        }
        if (objectsPending > 0)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        totals.ModulesImported++;

        // Surface a warning when source files were loaded but no
        // symbol-package objects matched any .al header — that means
        // either the .Source.zip doesn't contain the .al files for
        // those objects, or the file headers don't agree with the
        // symbol package's (Kind, Name) pairs (a Microsoft-side
        // packaging change). The example expected object + example
        // declaration help diagnose which case it is.
        if (filesByPath.Count > 0 && objectsExpectingSource > 0 && objectsLinked == 0)
        {
            var expectedObj = pkg.Symbols.Objects.FirstOrDefault(o => !string.IsNullOrEmpty(o.ReferenceSourceFileName));
            var declaredExample = declarations.Keys.FirstOrDefault();
            _logger.LogWarning(
                "Module {Name} {Version}: {FileCount} source file(s) loaded with {DeclCount} header declaration(s); "
                + "0/{Expected} symbol-package objects matched any .al header. "
                + "Expected example: {ExpectedKind} \"{ExpectedName}\". Declared example: {DeclaredKind} \"{DeclaredName}\". "
                + "The .Source.zip may not contain .al files for these objects, or the headers no longer match.",
                pkg.Manifest.Name, pkg.Manifest.Version,
                filesByPath.Count, declarations.Count, objectsExpectingSource,
                expectedObj?.Kind ?? "(none)", expectedObj?.Name ?? "(none)",
                declaredExample.Kind ?? "(none)", declaredExample.Name ?? "(none)");
        }

        // Clear the tracker between modules so a release-wide import doesn't
        // turn into an O(n²) walk over an ever-growing change-tracker.
        _db.ChangeTracker.Clear();
    }

    private const int FileChunkSize = 50;
    private const int ObjectChunkSize = 50;

    private void EmitSymbols(
        int orgId, OeModule module, OeModuleObject obj, SymbolObject symObj,
        IReadOnlyList<AlSymbol> extractedSymbols)
    {
        // The symbol package only ships public + internal methods; locals,
        // triggers, and event subscribers exist only in source. Index the
        // extractor's findings so the symbol-package loop below can stamp
        // line/column on matches, and so we can emit additional rows for
        // anything the extractor saw that the package omitted. Procedures
        // are kept as a queue per name so each overload of a method picks
        // up a distinct line — symbol-package method ordering tracks the
        // source declaration order in practice.
        var procQueueByName = new Dictionary<string, Queue<AlSymbol>>(StringComparer.OrdinalIgnoreCase);
        var fieldByName = new Dictionary<string, AlSymbol>(StringComparer.OrdinalIgnoreCase);
        var fieldById = new Dictionary<int, AlSymbol>();
        foreach (var sym in extractedSymbols)
        {
            switch (sym.Kind)
            {
                case "procedure":
                case "local_procedure":
                case "internal_procedure":
                case "protected_procedure":
                case "trigger":
                case "event_publisher":
                case "event_subscriber":
                    if (!procQueueByName.TryGetValue(sym.Name, out var queue))
                    {
                        queue = new Queue<AlSymbol>();
                        procQueueByName[sym.Name] = queue;
                    }
                    queue.Enqueue(sym);
                    break;
                case "field":
                    fieldByName.TryAdd(sym.Name, sym);
                    if (sym.FieldId is { } id) fieldById.TryAdd(id, sym);
                    break;
            }
        }

        var consumedExtracted = new HashSet<AlSymbol>();

        foreach (var method in symObj.Methods)
        {
            var kind = method.IsInternal ? "internal_procedure" : "procedure";
            int line = 0, colStart = 0, colEnd = 0;
            if (procQueueByName.TryGetValue(method.Name, out var queue) && queue.Count > 0)
            {
                var extracted = queue.Dequeue();
                consumedExtracted.Add(extracted);
                line = extracted.LineNumber;
                colStart = extracted.ColumnStart;
                colEnd = extracted.ColumnEnd;
                // event_publisher / event_subscriber / local_procedure /
                // protected_procedure carry more specific intent than the
                // package's IsInternal bit — prefer the extractor's kind
                // when it's one of those, but never downgrade
                // internal_procedure to plain procedure.
                if (extracted.Kind is "event_publisher" or "event_subscriber" or "protected_procedure")
                {
                    kind = extracted.Kind;
                }
                else if (extracted.Kind == "internal_procedure")
                {
                    kind = "internal_procedure";
                }
            }
            _db.OeModuleSymbols.Add(new OeModuleSymbol
            {
                OrganizationId = orgId,
                ModuleId = module.Id,
                Object = obj,
                Kind = kind,
                Name = method.Name,
                Signature = RenderSignature(method),
                ReturnType = method.ReturnType?.Name,
                LineNumber = line,
                ColumnStart = colStart,
                ColumnEnd = colEnd,
            });
        }

        foreach (var field in symObj.Fields)
        {
            int line = 0, colStart = 0, colEnd = 0;
            if (fieldById.TryGetValue(field.Id, out var extracted)
                || fieldByName.TryGetValue(field.Name, out extracted))
            {
                line = extracted.LineNumber;
                colStart = extracted.ColumnStart;
                colEnd = extracted.ColumnEnd;
            }
            _db.OeModuleSymbols.Add(new OeModuleSymbol
            {
                OrganizationId = orgId,
                ModuleId = module.Id,
                Object = obj,
                Kind = "field",
                Name = field.Name,
                Signature = field.Type.Name,
                FieldId = field.Id,
                LineNumber = line,
                ColumnStart = colStart,
                ColumnEnd = colEnd,
            });
        }

        // Locals / triggers / event subscribers / event publishers that the
        // compiler stripped from the symbol package — pick them up from
        // source so the outline shows them. consumedExtracted holds every
        // AlSymbol already mapped into a symbol-package row above, which
        // also correctly handles overloads (the queue dequeue gave each
        // package method a distinct extractor row).
        foreach (var sym in extractedSymbols)
        {
            switch (sym.Kind)
            {
                case "procedure":
                case "local_procedure":
                case "internal_procedure":
                case "protected_procedure":
                case "trigger":
                case "event_publisher":
                case "event_subscriber":
                    break;
                default:
                    continue;
            }
            if (consumedExtracted.Contains(sym)) continue;
            _db.OeModuleSymbols.Add(new OeModuleSymbol
            {
                OrganizationId = orgId,
                ModuleId = module.Id,
                Object = obj,
                Kind = sym.Kind,
                Name = sym.Name,
                Signature = sym.Signature,
                LineNumber = sym.LineNumber,
                ColumnStart = sym.ColumnStart,
                ColumnEnd = sym.ColumnEnd,
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

            // Funnel every layout through the shared canonicaliser so the
            // BC 28.x "<App Name>/src/..." wrapper and the BC 25.x
            // "src/..." form land on the same key as the symbol package's
            // ReferenceSourceFileName.
            var path = AppPackageReader.CanonicalizeSourcePath(entry.FullName);

            using var s = entry.Open();
            using var reader = new StreamReader(s);
            yield return (path, reader.ReadToEnd());
        }
    }

    // ── Source declaration scan ─────────────────────────────────────────

    /// <summary>
    /// One <c>(kind, name)</c> declaration found by scanning a
    /// <c>.al</c> file's header. The file reference plus the 1-based
    /// declaration line are both captured so the per-object loop can
    /// link <c>ModuleObject.SourceFileId</c> and stamp
    /// <c>ModuleObject.LineNumber</c> in one lookup.
    /// </summary>
    private readonly record struct DeclarationHit(OeModuleFile File, int Line);

    private sealed class DeclarationKeyComparer : IEqualityComparer<(string Kind, string Name)>
    {
        public static readonly DeclarationKeyComparer Instance = new();
        public bool Equals((string Kind, string Name) x, (string Kind, string Name) y)
            => string.Equals(x.Kind, y.Kind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Kind, string Name) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Kind),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }

    /// <summary>
    /// Walks every <c>.al</c> file once and indexes its top-level
    /// <c>&lt;kind&gt; [&lt;id&gt;] &lt;name&gt;</c> declaration by
    /// <c>(Kind, Name)</c>. The map drives the symbol-package
    /// <c>(Kind, Name) → ModuleFile</c> link, sidestepping the
    /// fragile <c>ReferenceSourceFileName</c> path-string lookup —
    /// the symbol package's path strings aren't consistent within a
    /// single BC release (some modules ship them with a nested
    /// <c>src/</c>, others with a project-folder prefix, others raw),
    /// so canonicalising the .Source.zip side alone can't bridge the
    /// gap. The header declaration is deterministic and AL enforces
    /// one object per file in practice. Multi-object files lose only
    /// the second-and-later objects' links (first match wins) —
    /// rare on first-party modules and acceptable for v1.
    /// </summary>
    private static Dictionary<(string Kind, string Name), DeclarationHit> ScanFileDeclarations(
        IReadOnlyDictionary<string, OeModuleFile> filesByPath)
    {
        var result = new Dictionary<(string, string), DeclarationHit>(DeclarationKeyComparer.Instance);
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
                result.TryAdd((kind, name), new DeclarationHit(file, line));
            }
        }
        return result;
    }

    /// <summary>
    /// Runs <see cref="AlSymbolExtractor.Extract"/> over every imported
    /// source file. The extractor is regex-based and cheap — one pass per
    /// file at import time replaces the historical "filled in by a
    /// source-scan pass later" placeholder and unblocks the outline panel.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<AlSymbol>> ExtractSubSymbolsByFile(
        IReadOnlyDictionary<string, OeModuleFile> filesByPath)
    {
        var result = new Dictionary<string, IReadOnlyList<AlSymbol>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, file) in filesByPath)
        {
            result[path] = AlSymbolExtractor.Extract(file.Content);
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

    // ── Phase-2 call-site extraction ───────────────────────────────────

    /// <summary>
    /// Runs <see cref="ALDevToolbox.Services.Al.AlReferenceExtractor"/>
    /// over every source file in the freshly-imported release and emits
    /// one <c>method_call</c> or <c>field_access</c> reference per
    /// resolved member access. Single-pass over the release's files; the
    /// type + member catalogs are built once up-front from data the
    /// per-module loop already wrote.
    ///
    /// Cross-release shadowing isn't handled here — a DK Core file
    /// calling <c>Customer.Insert()</c> against a Customer table that
    /// lives in a parent Base App release would drop the reference.
    /// In practice users tend to import every module they care about
    /// into one release (the BC DVD convention); cross-release call
    /// sites can be added later by reusing the recursive-CTE chain walk.
    /// </summary>
    private async Task EmitCallSiteReferencesAsync(
        int orgId, int releaseId, ImportTotals totals, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // (1) Build the type catalog: every object in this release keyed
        // by name (case-insensitive). Multiple objects can share a name
        // across object kinds (rare but legal — a Table and a Codeunit
        // both named "X"). Picking the first by Kind keeps it stable.
        var typeRows = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == releaseId)
            .Select(o => new
            {
                o.Id,
                o.Kind,
                o.ObjectId,
                o.Name,
                AppId = o.Module!.AppId,
            })
            .ToListAsync(ct);
        var typesByName = new Dictionary<string, ALDevToolbox.Services.Al.AlTypeRef>(StringComparer.OrdinalIgnoreCase);
        var objectIdByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in typeRows)
        {
            if (!typesByName.ContainsKey(t.Name))
            {
                typesByName[t.Name] = new ALDevToolbox.Services.Al.AlTypeRef(t.AppId, t.Kind, t.ObjectId, t.Name);
                objectIdByName[t.Name] = t.Id;
            }
        }

        // (2) Member catalog: for each owner Id, list its symbols.
        // Keyed by Id because owner names aren't unique across kinds.
        var memberRows = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Object!.Module!.ReleaseId == releaseId)
            .Select(s => new
            {
                OwnerId = s.Object!.Id,
                SymbolId = s.Id,
                s.Name,
                s.Kind,
                s.ReturnType,
            })
            .ToListAsync(ct);
        var membersByOwner = new Dictionary<long, List<MemberEntry>>();
        foreach (var m in memberRows)
        {
            if (!membersByOwner.TryGetValue(m.OwnerId, out var list))
            {
                list = new List<MemberEntry>();
                membersByOwner[m.OwnerId] = list;
            }
            // ReturnType is the raw "Record Customer" / "Code[20]" string
            // from the symbol package. Pull the AL type name out of it
            // so chained access can resolve through return types.
            var (retKw, retName) = ParseReturnType(m.ReturnType);
            list.Add(new MemberEntry(m.SymbolId, m.Name, m.Kind, retKw, retName));
        }

        // (3) Per-object globals from oe_module_variables. Keyed by
        // (objectId, lowered name). Built once; the per-file loop
        // grabs its file's owner-object id and filters.
        var varRows = await _db.OeModuleVariables.AsNoTracking()
            .Where(v => v.Object!.Module!.ReleaseId == releaseId)
            .Select(v => new
            {
                OwnerId = v.Object!.Id,
                v.Name,
                v.TypeKeyword,
                v.TypeName,
            })
            .ToListAsync(ct);
        var globalsByOwner = new Dictionary<long, Dictionary<string, ALDevToolbox.Services.Al.ResolvedVariableType>>();
        foreach (var v in varRows)
        {
            if (string.IsNullOrEmpty(v.TypeName)) continue;
            if (!globalsByOwner.TryGetValue(v.OwnerId, out var dict))
            {
                dict = new Dictionary<string, ALDevToolbox.Services.Al.ResolvedVariableType>(StringComparer.OrdinalIgnoreCase);
                globalsByOwner[v.OwnerId] = dict;
            }
            dict[v.Name] = new ALDevToolbox.Services.Al.ResolvedVariableType(v.TypeKeyword, v.TypeName);
        }

        // (4) Resolver: closes over the in-memory catalogs.
        var resolver = new CatalogResolver(typesByName, objectIdByName, membersByOwner);

        // (5) Walk every source file. For each, find its owner object
        // (the row in oe_module_objects whose source_file_id matches),
        // build the extract context, run the extractor, and emit
        // ModuleReference rows. Saved in chunks to keep tracker
        // pressure bounded.
        var files = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Module!.ReleaseId == releaseId)
            .Select(f => new
            {
                f.Id,
                f.Content,
                ModuleId = f.ModuleId,
                Owner = _db.OeModuleObjects
                    .Where(o => o.SourceFileId == f.Id)
                    .OrderBy(o => o.Id)
                    .Select(o => new { o.Id, o.Kind, o.Name, o.ObjectId, AppId = o.Module!.AppId })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        int totalEmitted = 0;
        int totalUnresolved = 0;
        int pending = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (file.Owner is null || string.IsNullOrEmpty(file.Content)) continue;

            globalsByOwner.TryGetValue(file.Owner.Id, out var globals);
            var ctx = new ALDevToolbox.Services.Al.AlExtractContext(
                OwnerKind: file.Owner.Kind,
                OwnerName: file.Owner.Name,
                OwnerObjectId: file.Owner.ObjectId,
                OwnerAppId: file.Owner.AppId,
                GlobalVars: globals ?? new Dictionary<string, ALDevToolbox.Services.Al.ResolvedVariableType>(StringComparer.OrdinalIgnoreCase),
                Resolver: resolver);

            var result = ALDevToolbox.Services.Al.AlReferenceExtractor.Extract(file.Content, ctx);
            totalUnresolved += result.Stats.UnresolvedReceivers;

            foreach (var r in result.References)
            {
                long? targetSymbolId = null;
                if (objectIdByName.TryGetValue(r.TargetObjectName, out var ownerId)
                    && membersByOwner.TryGetValue(ownerId, out var memberList))
                {
                    targetSymbolId = memberList.FirstOrDefault(m =>
                        string.Equals(m.Name, r.TargetMemberName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(m.Kind, r.TargetMemberKind, StringComparison.OrdinalIgnoreCase))?.SymbolId;
                }

                _db.OeModuleReferences.Add(new OeModuleReference
                {
                    OrganizationId = orgId,
                    ModuleId = file.ModuleId,
                    SourceObjectId = file.Owner.Id,
                    TargetAppId = r.TargetAppId,
                    TargetObjectKind = r.TargetObjectKind,
                    TargetObjectId = r.TargetObjectId,
                    TargetObjectName = r.TargetObjectName,
                    ReferenceKind = r.ReferenceKind,
                    LineNumber = r.Line,
                    TargetMemberName = r.TargetMemberName,
                    TargetMemberKind = r.TargetMemberKind,
                    TargetSymbolId = targetSymbolId,
                });
                totalEmitted++;
                pending++;
            }

            if (pending >= 500)
            {
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                _db.ChangeTracker.Clear();
                pending = 0;
            }
        }
        if (pending > 0)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
        }

        totals.ReferencesImported += totalEmitted;
        _logger.LogInformation(
            "Phase-2 call-site references: ReleaseId={ReleaseId} Files={Files} Emitted={Emitted} Unresolved={Unresolved} Elapsed={Elapsed}ms",
            releaseId, files.Count, totalEmitted, totalUnresolved, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Pulls the AL type out of a symbol-package return-type string
    /// like <c>"Record Customer"</c>, <c>"Code[20]"</c>,
    /// <c>"Codeunit \"Sales-Post\""</c>. Returns (null, null) for scalar
    /// types so the extractor's chained-access loop terminates on the
    /// next step.
    /// </summary>
    private static (string? Keyword, string? TypeName) ParseReturnType(string? returnType)
    {
        if (string.IsNullOrEmpty(returnType)) return (null, null);
        var trimmed = returnType.Trim();
        foreach (var kw in ReturnTypeKeywords)
        {
            if (trimmed.StartsWith(kw, StringComparison.OrdinalIgnoreCase)
                && trimmed.Length > kw.Length
                && char.IsWhiteSpace(trimmed[kw.Length]))
            {
                var rest = trimmed.Substring(kw.Length).TrimStart();
                if (rest.StartsWith('"') && rest.EndsWith('"') && rest.Length >= 2)
                {
                    return (kw, rest.Substring(1, rest.Length - 2));
                }
                return (kw, rest);
            }
        }
        return (null, null);
    }

    private static readonly string[] ReturnTypeKeywords = new[]
    {
        "Record", "Codeunit", "Page", "Report", "Query", "XmlPort",
        "Interface", "Enum", "RequestPage", "TestPage", "TestPart",
        "ControlAddIn", "PermissionSet", "Profile",
    };

    private sealed record MemberEntry(
        long SymbolId, string Name, string Kind, string? ReturnTypeKeyword, string? ReturnTypeName);

    /// <summary>
    /// IAlTypeResolver implementation backed by the in-memory catalogs
    /// built once per release. Translates the extractor's
    /// (typeName, ownerRef, memberName) lookups into dictionary hits.
    /// </summary>
    private sealed class CatalogResolver : ALDevToolbox.Services.Al.IAlTypeResolver
    {
        private readonly Dictionary<string, ALDevToolbox.Services.Al.AlTypeRef> _types;
        private readonly Dictionary<string, long> _objectIdByName;
        private readonly Dictionary<long, List<MemberEntry>> _members;

        public CatalogResolver(
            Dictionary<string, ALDevToolbox.Services.Al.AlTypeRef> types,
            Dictionary<string, long> objectIdByName,
            Dictionary<long, List<MemberEntry>> members)
        {
            _types = types;
            _objectIdByName = objectIdByName;
            _members = members;
        }

        public ALDevToolbox.Services.Al.AlTypeRef? ResolveTypeByName(string typeName)
            => _types.TryGetValue(typeName, out var t) ? t : null;

        public ALDevToolbox.Services.Al.AlMember? ResolveMember(
            ALDevToolbox.Services.Al.AlTypeRef owner, string memberName)
        {
            if (!_objectIdByName.TryGetValue(owner.Name, out var ownerId)) return null;
            if (!_members.TryGetValue(ownerId, out var list)) return null;
            var match = list.FirstOrDefault(m =>
                string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase));
            if (match is null) return null;
            return new ALDevToolbox.Services.Al.AlMember(
                Name: match.Name,
                Kind: match.Kind,
                ReturnTypeKeyword: match.ReturnTypeKeyword,
                ReturnTypeName: match.ReturnTypeName);
        }
    }
}
