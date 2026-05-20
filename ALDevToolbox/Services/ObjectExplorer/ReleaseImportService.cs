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
    private readonly StorageQuotaGuard _quotaGuard;
    private readonly TranslationImportService _translations;
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
        StorageQuotaGuard quotaGuard,
        TranslationImportService translations,
        ILogger<ReleaseImportService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _quotaGuard = quotaGuard;
        _translations = translations;
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
        await _quotaGuard.EnsureCanWriteAsync(ct).ConfigureAwait(false);
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

            // SourceTable propagation for pageextensions. The symbol
            // package only carries SourceTable on the base page; the
            // extension inherits it implicitly but ships no property of
            // its own. Copy it across now so the reference extractor's
            // page-Rec resolution (BuildGlobalScope) finds it for
            // pageextensions too.
            await PropagateSourceTableToPageExtensionsAsync(release.Id, ct).ConfigureAwait(false);

            // SourceTable property values in modern BC (28.x+) ship as
            // bare numeric ids ("36" for Sales Header) rather than the
            // legacy `#<appid>#<name>` hash-ref format. Resolve any
            // numeric values to the table's name so the reference
            // extractor can ResolveTypeByName on it. Runs AFTER the
            // pageextension propagation so any propagated numeric values
            // get normalised too.
            await ResolveNumericSourceTableNamesAsync(release.Id, ct).ConfigureAwait(false);

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

            // Stamp the denormalised file count + content-size totals so the
            // Releases picker doesn't recompute them via correlated subqueries
            // on every page load. The file set is immutable after a Release
            // goes ready, so a single snapshot here is enough.
            var totalsRow = await _db.OeModuleFiles.AsNoTracking()
                .Where(f => f.Module!.ReleaseId == release.Id)
                .GroupBy(_ => 1)
                .Select(g => new { Count = g.Count(), Length = g.Sum(f => (long)f.Content.Length) })
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
            ready.SourceFileCount = totalsRow?.Count ?? 0;
            ready.SourceContentLength = totalsRow?.Length ?? 0;

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
                SourceFilesImported: totals.SourceFilesImported,
                TranslationsImported: 0);
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
        //
        // Both branches dedupe by path with last-write-wins. Microsoft's
        // System.app (observed on BC 28.1) ships duplicate entries that
        // normalise to the same canonical path (e.g. two src/dotnet.al's);
        // failing the entire 110-module import on a content collision the
        // user can't fix isn't worth it, so we keep one and log a warning.
        IReadOnlyDictionary<string, string> sourceFiles;
        if (upload.SourceZipStream is not null)
        {
            var fromZip = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, content) in ReadSourceZip(upload.SourceZipStream))
            {
                if (fromZip.ContainsKey(path))
                {
                    _logger.LogWarning(
                        "Duplicate source path in .Source.zip for {Module}: {Path} — keeping last occurrence",
                        pkg.Manifest.Name, path);
                }
                fromZip[path] = content;
            }
            sourceFiles = fromZip;
        }
        else
        {
            var fromEmbedded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in pkg.SourceFiles)
            {
                if (fromEmbedded.ContainsKey(file.Path))
                {
                    _logger.LogWarning(
                        "Duplicate embedded source path in {Module}: {Path} — keeping last occurrence",
                        pkg.Manifest.Name, file.Path);
                }
                fromEmbedded[file.Path] = file.Content;
            }
            sourceFiles = fromEmbedded;
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
                // SourceTable on pages — extracted from the symbol package's
                // property list. Pageextensions don't carry it directly; a
                // second pass below copies the value from their base page.
                SourceTableName = ExtractSourceTableName(symObj),
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
            EmitVariables(orgId, module, obj, symObj, extractedForObject);
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

        // Translations are NOT extracted from .app files during release
        // ingest. The base-app XLIFFs are large enough that the DOM
        // parser (XDocument.Load inside AlXliffParser) ran the import
        // container out of memory. Admins upload XLIFFs on demand
        // through TranslationImportService — see the note in
        // AppPackageReader for the longer rationale.

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
                case "table_field":
                    // Only table-side fields ship in symObj.Fields, so only
                    // table_field needs to seed the dedup index. Page fields
                    // (page_field, emitted by the source extractor) never
                    // collide here — they fall through to the source-only
                    // re-emission loop below.
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
                // Mark the extracted field row consumed so the
                // page-field pass below doesn't re-emit it. Table-side
                // fields ship in symObj.Fields; page-side ones don't, so
                // we only need the dedup on table flows.
                consumedExtracted.Add(extracted);
            }
            _db.OeModuleSymbols.Add(new OeModuleSymbol
            {
                OrganizationId = orgId,
                ModuleId = module.Id,
                Object = obj,
                // symObj.Fields only carries table-side fields — page
                // fields aren't in symbol packages — so the persisted
                // kind is always table_field here. See
                // .design/al-reference-extractor-refactor.md step 1.
                Kind = "table_field",
                Name = field.Name,
                Signature = field.Type.Name,
                FieldId = field.Id,
                LineNumber = line,
                ColumnStart = colStart,
                ColumnEnd = colEnd,
            });
        }

        // Locals / triggers / event subscribers / event publishers / page
        // fields / actions that the symbol package omits — pick them up
        // from the source extractor so the outline shows them.
        // consumedExtracted holds every AlSymbol already mapped into a
        // symbol-package row above, which also correctly handles
        // overloads (the queue dequeue gave each package method a distinct
        // extractor row) and table-field/page-field disambiguation
        // (table fields enter symObj.Fields and consume their matching
        // extractor row; page fields don't, so they fall through here).
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
                case "table_field":
                case "page_field":
                case "page_action":
                case "query_column":
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
                FieldId = sym.FieldId,
                LineNumber = sym.LineNumber,
                ColumnStart = sym.ColumnStart,
                ColumnEnd = sym.ColumnEnd,
            });
        }
    }

    private void EmitVariables(
        int orgId, OeModule module, OeModuleObject obj, SymbolObject symObj,
        IReadOnlyList<AlSymbol> extractedSymbols)
    {
        // Symbol packages carry variable name + type but not source
        // positions; the source extractor's var_declaration rows fill
        // that gap. First-declaration-wins on name collisions — in
        // practice, object-scope globals appear in the file before any
        // procedure-local var with the same name. See
        // .design/al-reference-extractor-refactor.md step 2.
        var positionsByName = new Dictionary<string, AlSymbol>(StringComparer.OrdinalIgnoreCase);
        foreach (var sym in extractedSymbols)
        {
            if (sym.Kind != "var_declaration") continue;
            positionsByName.TryAdd(sym.Name, sym);
        }

        foreach (var variable in symObj.Variables)
        {
            var (targetKind, targetId, targetName, typeKeyword) = ResolveVariableTarget(variable.Type, module.AppId);
            positionsByName.TryGetValue(variable.Name, out var pos);
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
                LineNumber = pos?.LineNumber ?? 0,
                ColumnStart = pos?.ColumnStart ?? 0,
                ColumnEnd = pos?.ColumnEnd ?? 0,
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

    /// <summary>
    /// Pulls the SourceTable property value out of a page /
    /// pageextension's symbol-package properties. The value shape has
    /// drifted across BC versions:
    /// <list type="bullet">
    ///   <item>Legacy (pre-28.x): <c>#&lt;32hex&gt;#&lt;name&gt;</c>
    ///         hash-ref — same shape as <c>TableNo</c> / <c>ExtendsTarget</c>.
    ///         <see cref="ParseHashRef"/> extracts the name.</item>
    ///   <item>Modern (28.x+): bare numeric object id (<c>"36"</c> for
    ///         Sales Header). We pass it through and let
    ///         <see cref="ResolveNumericSourceTableNamesAsync"/> swap
    ///         it for the table's name after all tables in the release
    ///         are imported.</item>
    ///   <item>Some packages emit the bare name. Pass-through too.</item>
    /// </list>
    /// Returns null for kinds without the property. Pageextensions
    /// don't carry SourceTable in their own properties — they inherit
    /// it from the base page, which a second-pass copy
    /// (<see cref="PropagateSourceTableToPageExtensionsAsync"/>) fills.
    /// </summary>
    private static string? ExtractSourceTableName(SymbolObject symObj)
    {
        // Three properties bind Rec to a specific table:
        //   - SourceTable on a page / pageextension
        //   - TableNo on a codeunit (sets Rec when the codeunit is run
        //     as the OnRun trigger receiver — `codeunit "Gen. Jnl.-Post"`
        //     with `TableNo = "Gen. Journal Line"` runs against a
        //     journal-line record, with Rec bound to that table inside
        //     OnRun and any procedures called from it)
        // We funnel all three through the same column on oe_module_objects
        // (source_table_name); the AL extractor binds Rec to whatever
        // table is named there regardless of which property populated it.
        var propName = symObj.Kind switch
        {
            "page" or "pageextension" => "SourceTable",
            "codeunit" => "TableNo",
            _ => null,
        };
        if (propName is null) return null;

        var prop = symObj.Properties.FirstOrDefault(p =>
            string.Equals(p.Name, propName, StringComparison.OrdinalIgnoreCase));
        if (prop is null) return null;
        var (_, name) = ParseHashRef(prop.Value);
        // Some symbol packages emit the bare table name when the table
        // lives in the same module; modern BC ships the numeric object
        // id. Accept all three shapes — ResolveNumericSourceTableNamesAsync
        // normalises the numeric form to a name after import.
        return name ?? (string.IsNullOrEmpty(prop.Value) ? null : prop.Value);
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

    /// <summary>
    /// Second pass over the just-imported release: for each pageextension,
    /// look up the page it extends (by ExtendsAppId + ExtendsObjectName,
    /// considering modules visible to the extension's module) and copy the
    /// base page's <c>SourceTableName</c> onto the extension. Done as a
    /// single UPDATE … FROM SQL because EF Core can't express the
    /// self-join cleanly and the per-row navigate-update approach would
    /// be hundreds of tracker-loaded entities for a large release.
    /// </summary>
    private async Task PropagateSourceTableToPageExtensionsAsync(int releaseId, CancellationToken ct)
    {
        // Match by ExtendsObjectName against any base page in the same
        // release. Cross-release base-page lookups (a customer release
        // extending a Base App page from a parent release) are deferred
        // alongside the broader cross-release-shadowing gap — pageextension
        // .al source in the layered case is rare and the extractor
        // gracefully falls back to "Rec is the page itself" (still wrong,
        // still won't underline, but no crash).
        // Postgres UPDATE … FROM doesn't let the target alias (`ext`)
        // appear in an inner JOIN's ON clause — only in WHERE. So we
        // gate the extension's own release membership via a subquery
        // instead of joining oe_modules a second time on `ext.module_id`.
        const string sql = """
            UPDATE oe_module_objects ext
            SET source_table_name = base.source_table_name
            FROM oe_module_objects base
            JOIN oe_modules base_mod ON base_mod.id = base.module_id
            WHERE ext.kind = 'pageextension'
              AND ext.source_table_name IS NULL
              AND ext.extends_object_name IS NOT NULL
              AND base.kind = 'page'
              AND base.name = ext.extends_object_name
              AND base.source_table_name IS NOT NULL
              AND base_mod.release_id = {0}
              AND ext.module_id IN (
                  SELECT id FROM oe_modules WHERE release_id = {0}
              );
            """;
        await _db.Database.ExecuteSqlRawAsync(sql, new object[] { releaseId }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Modern BC (28.x and later) emits a page's <c>SourceTable</c>
    /// property in the symbol package as the bare numeric object id
    /// (<c>"36"</c> for Sales Header), not the legacy
    /// <c>#&lt;appid&gt;#&lt;name&gt;</c> hash-ref format
    /// <see cref="ExtractSourceTableName"/> was originally written for.
    /// We pass the raw value through and resolve it here: for every
    /// page / pageextension in this release whose
    /// <c>source_table_name</c> is digit-only, look up the table with
    /// that <c>object_id</c> in the same release and replace the
    /// numeric value with the table's <c>name</c>.
    ///
    /// Same-release scoping is intentional. Tables in parent releases
    /// (cross-release shadowing) aren't reachable yet — that's gap #3
    /// in <c>al-reference-extractor-gaps.md</c>; the current pageext
    /// dependency-aware-resolver work would need a similar lift.
    ///
    /// Done as a single UPDATE … FROM for the same reason
    /// <see cref="PropagateSourceTableToPageExtensionsAsync"/> uses one:
    /// EF Core can't express the self-join cleanly and the per-row path
    /// is a tracker load for each page in a busy release.
    /// </summary>
    private async Task ResolveNumericSourceTableNamesAsync(int releaseId, CancellationToken ct)
    {
        // Filter includes codeunit alongside page / pageextension —
        // codeunits get source_table_name from their TableNo property
        // (a codeunit with TableNo binds Rec to the named table when run).
        const string sql = """
            UPDATE oe_module_objects pg
            SET source_table_name = t.name
            FROM oe_module_objects t
            JOIN oe_modules tm ON tm.id = t.module_id
            WHERE pg.kind IN ('page', 'pageextension', 'codeunit')
              AND pg.source_table_name ~ '^[0-9]+$'
              AND t.kind = 'table'
              AND t.object_id = pg.source_table_name::int
              AND tm.release_id = {0}
              AND pg.module_id IN (
                  SELECT id FROM oe_modules WHERE release_id = {0}
              );
            """;
        await _db.Database.ExecuteSqlRawAsync(sql, new object[] { releaseId }, ct).ConfigureAwait(false);

        // Second pass: numeric SourceTable values that fall in the BC
        // platform-table id range (2000000001 – 2000000999) don't match
        // any real table in oe_module_objects (the platform tables live
        // in the AL runtime, not in any module's symbol package). Walk
        // the PlatformVirtualTables map and rewrite the source_table_name
        // for any matching numeric value. Without this, every page with
        // `SourceTable = 2000000200` (NAV App Installed App) leaves
        // source_table_name as the numeric string, Rec binding becomes
        // `Record 2000000200`, and the chain-walker logs head-var-type-
        // unresolved on every Rec.X access.
        const string platformSql = """
            UPDATE oe_module_objects
            SET source_table_name = {0}
            WHERE source_table_name = {1}
              AND module_id IN (SELECT id FROM oe_modules WHERE release_id = {2});
            """;
        foreach (var vt in PlatformVirtualTables)
        {
            await _db.Database.ExecuteSqlRawAsync(
                platformSql,
                new object[] { vt.Name, vt.Id.ToString(), releaseId },
                ct).ConfigureAwait(false);
        }
    }

    // ── Mutable tally ───────────────────────────────────────────────────

    private sealed class ImportTotals
    {
        public int ModulesImported;
        public int ModulesSkipped;
        public int ObjectsImported;
        public int ReferencesImported;
        public int SourceFilesImported;
        // No `TranslationsImported` here — translations are no longer
        // auto-extracted during release ingest. The public
        // ReleaseImportSummary still surfaces the field (always 0)
        // so existing callers keep compiling; admins drive the count
        // up via TranslationImportService's explicit upload paths.
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
        // across kinds and modules — e.g. Microsoft's Subscription
        // Billing app declares a tableextension named "Sales Header"
        // alongside the actual Sales Header table in Base Application.
        // We store every candidate per name; the resolver chooses by
        // visibility + non-extension preference at lookup time.
        //
        // Identity-keyed object-id lookup lets the post-extraction
        // TargetSymbolId stamp (and the resolver's member lookup) find
        // the right oe_module_objects row even when multiple objects
        // share a name. The composite key (AppId, Kind, Name) is the
        // catalog's canonical identity.
        var typeRows = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == releaseId)
            .Select(o => new
            {
                o.Id,
                o.Kind,
                o.ObjectId,
                o.Name,
                AppId = o.Module!.AppId,
                o.SourceTableName,
            })
            .ToListAsync(ct);
        var typesByName = new Dictionary<string, List<ALDevToolbox.Services.Al.AlTypeRef>>(StringComparer.OrdinalIgnoreCase);
        var typesByObjectId = new Dictionary<long, ALDevToolbox.Services.Al.AlTypeRef>();
        var objectIdByIdentity = new Dictionary<(Guid AppId, string Kind, string Name), long>(
            new ObjectIdentityComparer());
        // Per-object source-table lookup so AlPageStructure can
        // resolve cross-page SubPageLink / RunPageLink field
        // references in step 5 — the LHS field name belongs to the
        // TARGET page's source table, not the current page's Rec.
        // Only populated for objects that have a SourceTable
        // (page / pageextension / requestpage / report-dataitem in
        // practice); other kinds skip the dictionary entry.
        var sourceTablesByObjectId = new Dictionary<long, string>();
        foreach (var t in typeRows)
        {
            var typeRef = new ALDevToolbox.Services.Al.AlTypeRef(t.AppId, t.Kind, t.ObjectId, t.Name);
            typesByObjectId[t.Id] = typeRef;
            if (!typesByName.TryGetValue(t.Name, out var list))
            {
                list = new List<ALDevToolbox.Services.Al.AlTypeRef>();
                typesByName[t.Name] = list;
            }
            list.Add(typeRef);
            objectIdByIdentity[(t.AppId, t.Kind, t.Name)] = t.Id;
            if (!string.IsNullOrEmpty(t.SourceTableName))
            {
                sourceTablesByObjectId[t.Id] = t.SourceTableName;
            }
        }

        // (1b) Synthesise catalog entries for BC platform virtual tables.
        // These live in the AL runtime (ids 2000000001 – 2000000999 reserved),
        // not in any module's symbol package, but extensions reference them
        // freely: `TempFieldSet: Record Field;`, `User.SetRange("User Name", X);`.
        // Without synthesis the type lookup fails and the chain is logged
        // as an unresolved variable type — even though the reference is
        // legitimate runtime API. Stamped with PlatformAppId (Guid.Empty)
        // so visibility-aware resolvers can recognise them; the resolver
        // already treats PlatformAppId as visible to everyone via the
        // FoundationalAppNames-style implicit-visibility rule (see below).
        foreach (var vt in PlatformVirtualTables)
        {
            var typeRef = new ALDevToolbox.Services.Al.AlTypeRef(PlatformAppId, "table", vt.Id, vt.Name);
            if (!typesByName.TryGetValue(vt.Name, out var list))
            {
                list = new List<ALDevToolbox.Services.Al.AlTypeRef>();
                typesByName[vt.Name] = list;
            }
            list.Add(typeRef);
            // No oe_module_objects.Id for synthetic entries — typesByObjectId
            // and objectIdByIdentity stay unaugmented (they're keyed off
            // the DB row id, which doesn't exist here). The chain walker
            // only needs typesByName + RecordMethods to resolve calls
            // like `TempFieldSet.GET(...)` against these tables.
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
                s.LineNumber,
            })
            .ToListAsync(ct);
        var membersByOwner = new Dictionary<long, List<MemberEntry>>();
        // (Owner, LineNumber) → SymbolId. Used by the reference loop to
        // resolve source_symbol_id (the calling procedure / trigger) and
        // by the scope-tracking pass to attach end_line / end_column onto
        // the right symbol row without a name+kind ambiguity dance —
        // line is unique within an object. See issues #180 / #181.
        var symbolIdByOwnerAndLine = new Dictionary<(long OwnerId, int LineNumber), long>();
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
            if (m.LineNumber > 0)
            {
                symbolIdByOwnerAndLine[(m.OwnerId, m.LineNumber)] = m.SymbolId;
            }
        }

        // (3) Per-object globals from oe_module_variables. Keyed by
        // (objectId, lowered name). Built once; the per-file loop
        // grabs its file's owner-object id and filters.
        var varRows = await _db.OeModuleVariables.AsNoTracking()
            .Where(v => v.Object!.Module!.ReleaseId == releaseId)
            .Select(v => new
            {
                OwnerId = v.Object!.Id,
                v.Id,
                v.Name,
                v.TypeKeyword,
                v.TypeName,
            })
            .ToListAsync(ct);
        var globalsByOwner = new Dictionary<long, Dictionary<string, ALDevToolbox.Services.Al.ResolvedVariableType>>();
        // Per-(owner, lowered name) → variable id lookup so the
        // reference-emit loop can stamp TargetVariableId on
        // variable_use rows (step 6). Built alongside globalsByOwner
        // to share the single varRows scan.
        var variableIdByOwnerAndName = new Dictionary<(long OwnerId, string Name), long>();
        foreach (var v in varRows)
        {
            variableIdByOwnerAndName[(v.OwnerId, v.Name.ToLowerInvariant())] = v.Id;
            if (string.IsNullOrEmpty(v.TypeName)) continue;
            if (!globalsByOwner.TryGetValue(v.OwnerId, out var dict))
            {
                dict = new Dictionary<string, ALDevToolbox.Services.Al.ResolvedVariableType>(StringComparer.OrdinalIgnoreCase);
                globalsByOwner[v.OwnerId] = dict;
            }
            dict[v.Name] = new ALDevToolbox.Services.Al.ResolvedVariableType(v.TypeKeyword, v.TypeName);
        }

        // (4) Extensions-by-base index: for each tableextension /
        // pageextension / etc., record the base object name it targets
        // plus the extension's own AppId + ObjectId. The resolver
        // consults this map when a member lookup misses on the base —
        // a procedure added via CustomerExt should be findable as a
        // method on Customer-typed receivers, subject to visibility.
        var extRows = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == releaseId)
            .Where(o => o.Kind == "tableextension"
                     || o.Kind == "pageextension"
                     || o.Kind == "reportextension"
                     || o.Kind == "enumextension"
                     || o.Kind == "permissionsetextension")
            .Where(o => o.ExtendsObjectName != null)
            .Select(o => new
            {
                o.Id,
                ExtensionAppId = o.Module!.AppId,
                BaseName = o.ExtendsObjectName!,
            })
            .ToListAsync(ct);
        var extensionsByBaseName = new Dictionary<string, List<ExtensionEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in extRows)
        {
            if (!extensionsByBaseName.TryGetValue(e.BaseName, out var list))
            {
                list = new List<ExtensionEntry>();
                extensionsByBaseName[e.BaseName] = list;
            }
            list.Add(new ExtensionEntry(e.ExtensionAppId, e.Id));
        }

        // (5) Per-module visibility: which AppIds is each module
        // allowed to reach via app.json dependencies (transitively).
        // Object resolution and extension-member lookup both filter
        // through this so a Base App file can't reach into DK Core,
        // a third-party extension can't reach into an unrelated
        // third-party extension, etc.
        var moduleVisibility = await BuildModuleVisibilityAsync(releaseId, ct);

        // (6) Per-module resolver cache. All files in the same module
        // share the same visibility set, so build the resolver once
        // and reuse across files.
        var resolversByModule = new Dictionary<long, ALDevToolbox.Services.Al.IAlTypeResolver>();
        ALDevToolbox.Services.Al.IAlTypeResolver ResolverFor(long moduleId)
        {
            if (resolversByModule.TryGetValue(moduleId, out var cached)) return cached;
            moduleVisibility.TryGetValue(moduleId, out var visible);
            var r = new CatalogResolver(
                typesByName, typesByObjectId, objectIdByIdentity,
                membersByOwner, extensionsByBaseName, sourceTablesByObjectId, visible);
            resolversByModule[moduleId] = r;
            return r;
        }

        // (7) Walk every source file. For each, find its owner object
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
                f.Path,
                ModuleId = f.ModuleId,
                ModuleName = f.Module!.Name,
                Owner = _db.OeModuleObjects
                    .Where(o => o.SourceFileId == f.Id)
                    .OrderBy(o => o.Id)
                    .Select(o => new
                    {
                        o.Id,
                        o.Kind,
                        o.Name,
                        o.ObjectId,
                        AppId = o.Module!.AppId,
                        o.SourceTableName,
                        o.ExtendsObjectName,
                    })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        int totalEmitted = 0;
        int totalUnresolved = 0;
        int pending = 0;
        // Diagnostic bucket: first N unresolved references seen across all
        // files in this phase. Capped low so a pathological release doesn't
        // bloat the log; the per-file extractor also caps internally so
        // late files still get a chance to contribute samples even when
        // earlier files were noisy.
        const int unresolvedLogCap = 50;
        var unresolvedSamples = new List<(string Module, string Path, string Owner, ALDevToolbox.Services.Al.UnresolvedSample Sample)>(unresolvedLogCap);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (file.Owner is null || string.IsNullOrEmpty(file.Content)) continue;

            globalsByOwner.TryGetValue(file.Owner.Id, out var globals);
            // For tableextensions, Rec is semantically the BASE TABLE
            // (the extension's columns are merged into the base at
            // runtime). The base table's name lives in ExtendsObjectName
            // — we feed it through OwnerSourceTableName so BuildGlobalScope
            // wires Rec to (Record, base table). ResolveMember on the
            // base table then walks base → visible extensions, which
            // covers all three cases (base-declared, this-extension-
            // declared, sibling-extension-declared).
            var sourceTable = file.Owner.SourceTableName;
            if (string.IsNullOrEmpty(sourceTable) && file.Owner.Kind == "tableextension")
            {
                sourceTable = file.Owner.ExtendsObjectName;
            }
            // Pageextension fallback: PropagateSourceTableToPageExtensionsAsync
            // copies the base page's source_table_name into the
            // extension at import end, but the join is same-release-only
            // and requires the base page's source_table_name to be set.
            // When either misses (cross-release base page, or the base
            // page's source table wasn't extracted), Rec doesn't get
            // wired in BuildGlobalScope and every `Rec.X` chain in the
            // body fires head-not-a-variable. Catch the miss here by
            // looking the base page up via the resolver — which catalogs
            // every page in the current release — and asking for its
            // source-table name through the IAlTypeResolver hook added
            // in step 5.
            if (string.IsNullOrEmpty(sourceTable)
                && file.Owner.Kind == "pageextension"
                && !string.IsNullOrEmpty(file.Owner.ExtendsObjectName))
            {
                var pextResolver = ResolverFor(file.ModuleId);
                var basePage = pextResolver.ResolveTypeByName(file.Owner.ExtendsObjectName!, "Page");
                if (basePage is not null)
                {
                    sourceTable = pextResolver.ResolveSourceTableName(basePage);
                }
            }
            var ctx = new ALDevToolbox.Services.Al.AlExtractContext(
                OwnerKind: file.Owner.Kind,
                OwnerName: file.Owner.Name,
                OwnerObjectId: file.Owner.ObjectId,
                OwnerAppId: file.Owner.AppId,
                GlobalVars: globals ?? new Dictionary<string, ALDevToolbox.Services.Al.ResolvedVariableType>(StringComparer.OrdinalIgnoreCase),
                Resolver: ResolverFor(file.ModuleId),
                OwnerSourceTableName: sourceTable);

            var result = ALDevToolbox.Services.Al.AlReferenceExtractor.Extract(file.Content, ctx);
            totalUnresolved += result.Stats.UnresolvedReceivers;

            // Diagnostic sampling: capture the first N unresolved
            // tokens across the whole phase so operators can spot
            // systematic gaps (a common token shape, an uningested
            // dependency, …) without re-running with verbose logging.
            // Cap at perFileSampleCap per file so one noisy file
            // doesn't consume the whole bucket — we'd rather see
            // patterns across many files than 50 lines from 3 files.
            const int perFileSampleCap = 3;
            if (unresolvedSamples.Count < unresolvedLogCap
                && result.Stats.UnresolvedSamples.Count > 0)
            {
                int fromThisFile = 0;
                foreach (var s in result.Stats.UnresolvedSamples)
                {
                    if (unresolvedSamples.Count >= unresolvedLogCap) break;
                    if (fromThisFile >= perFileSampleCap) break;
                    unresolvedSamples.Add((
                        file.ModuleName ?? string.Empty,
                        file.Path ?? string.Empty,
                        file.Owner.Kind + ":" + file.Owner.Name,
                        s));
                    fromThisFile++;
                }
            }

            // Stamp end_line / end_column onto the body-bearing symbols
            // the walker just finished tracing through. The extractor
            // emits one ExtractedSymbolScope per (procedure / trigger /
            // event publisher / event subscriber) on body close; we
            // resolve back to the symbol row by (owner, start line)
            // since line is unique within an object. Attach + mark
            // modified so EF emits a targeted UPDATE for these two
            // columns only — no full-row reload. See issue #181.
            foreach (var scope in result.SymbolScopes)
            {
                if (!symbolIdByOwnerAndLine.TryGetValue(
                        (file.Owner.Id, scope.StartLine),
                        out var scopeSymbolId))
                {
                    continue;
                }
                var stub = new OeModuleSymbol
                {
                    Id = scopeSymbolId,
                    EndLine = scope.EndLine,
                    EndColumn = scope.EndColumn,
                };
                _db.OeModuleSymbols.Attach(stub);
                _db.Entry(stub).Property(s => s.EndLine).IsModified = true;
                _db.Entry(stub).Property(s => s.EndColumn).IsModified = true;
                pending++;
            }

            foreach (var r in result.References)
            {
                long? targetSymbolId = null;
                long? targetVariableId = null;
                // Resolve the owning procedure / trigger that emitted this
                // reference — the (Owner, StartLine) tuple uniquely
                // identifies the symbol row. Null for object-scope refs
                // and for legacy / pre-#181 ingests where the extractor
                // didn't stamp scope onto ExtractedReference.
                long? sourceSymbolId = null;
                if (r.SourceMemberLine is int sourceLine
                    && symbolIdByOwnerAndLine.TryGetValue(
                        (file.Owner.Id, sourceLine),
                        out var resolvedSourceSymbolId))
                {
                    sourceSymbolId = resolvedSourceSymbolId;
                }
                // Identity-keyed lookup so a tableextension named the
                // same as the table it extends doesn't claim the table's
                // symbols at TargetSymbolId stamp time. The reference row
                // carries TargetAppId + TargetObjectKind + TargetObjectName
                // — that's exactly the catalog's canonical identity.
                if (objectIdByIdentity.TryGetValue(
                        (r.TargetAppId, r.TargetObjectKind, r.TargetObjectName),
                        out var ownerId)
                    && membersByOwner.TryGetValue(ownerId, out var memberList))
                {
                    targetSymbolId = memberList.FirstOrDefault(m =>
                        string.Equals(m.Name, r.TargetMemberName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(m.Kind, r.TargetMemberKind, StringComparison.OrdinalIgnoreCase))?.SymbolId;
                }

                // Stamp variable_use rows with the resolved
                // oe_module_variables FK so right-click "Find
                // references" on a global lands on the filtered
                // index (ix_oe_module_references_target_variable).
                // The extractor targets the file's owner with
                // TargetMemberName = variable name; we look up the
                // DB id by (owner, name). See step 6.
                if (string.Equals(r.ReferenceKind, "variable_use", StringComparison.Ordinal)
                    && r.TargetMemberName is not null
                    && variableIdByOwnerAndName.TryGetValue(
                        (file.Owner.Id, r.TargetMemberName.ToLowerInvariant()),
                        out var variableId))
                {
                    targetVariableId = variableId;
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
                    ColumnNumber = r.Column,
                    TargetMemberName = r.TargetMemberName,
                    TargetMemberKind = r.TargetMemberKind,
                    TargetSymbolId = targetSymbolId,
                    TargetVariableId = targetVariableId,
                    SourceSymbolId = sourceSymbolId,
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

        if (unresolvedSamples.Count > 0)
        {
            // One log line per sample so existing grep/structured-log
            // tooling can slice by Reason without parsing a multi-line
            // entry. Includes the file's module+path+owner so the dev
            // can open the source viewer to inspect the token in
            // context. Capped at unresolvedLogCap (see above).
            foreach (var (module, path, owner, sample) in unresolvedSamples)
            {
                _logger.LogInformation(
                    "Phase-2 unresolved sample: ReleaseId={ReleaseId} Reason={Reason} Token='{Token}' Line={Line} Col={Col} Owner={Owner} ReceiverKind={ReceiverKind} ReceiverName='{ReceiverName}' ReceiverAppId={ReceiverAppId} Module={Module} Path={Path}",
                    releaseId,
                    sample.Reason,
                    sample.Token,
                    sample.Line,
                    sample.Column,
                    owner,
                    sample.ReceiverKind ?? "(n/a)",
                    sample.ReceiverName ?? string.Empty,
                    sample.ReceiverAppId?.ToString() ?? "(n/a)",
                    module,
                    path);
            }
        }
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

    /// <summary>
    /// For each module in the release, compute the transitive set of
    /// AppIds it can legally reach via <c>app.json</c> dependencies
    /// (plus the module's own AppId, plus the well-known foundational
    /// Microsoft apps every extension implicitly resolves). The
    /// reference-extractor's resolver consults this set so a file in
    /// DK Core can resolve types from Base App (an implicit dep) but
    /// not from OIOUBL (an unrelated extension).
    ///
    /// <para><b>Implicit foundational apps.</b> AL extensions never
    /// declare dependencies on System Application, Base Application,
    /// Application, or Business Foundation — the AL compiler always
    /// makes their symbols available, and Microsoft confirmed this
    /// matches the developer-tool experience. AMC Banking 365
    /// Fundamentals (and ~every BC extension) ships with empty
    /// <c>&lt;Dependencies/&gt;</c> in <c>NavxManifest.xml</c> yet
    /// freely references <c>Codeunit "Temp Blob"</c> (System App),
    /// <c>Record "Sales Header"</c> (Base App), etc. The visibility
    /// set must mirror that or every such reference looks
    /// "type-unresolved" from the resolver's perspective.
    ///
    /// Modules whose <c>DependenciesJson</c> references AppIds outside
    /// this release land in the visibility set anyway — cross-release
    /// receivers don't resolve in this pass but the set correctly
    /// captures intent.
    /// </summary>
    private async Task<Dictionary<long, HashSet<Guid>>> BuildModuleVisibilityAsync(
        int releaseId, CancellationToken ct)
    {
        var modules = await _db.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == releaseId)
            .Select(m => new { m.Id, m.AppId, m.Name, m.Publisher, m.DependenciesJson })
            .ToListAsync(ct);

        // Each module's direct deps as parsed from DependenciesJson.
        var directDepsByAppId = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var m in modules)
        {
            directDepsByAppId[m.AppId] = ParseDependencyAppIds(m.DependenciesJson);
        }

        // The implicit foundational set: Microsoft-published modules in
        // this release whose name matches one of the always-available
        // platform apps. Matched by name so the GUID can drift across
        // BC versions (Microsoft has historically restamped these).
        // Publisher filter keeps a hypothetical third-party app called
        // "Base Application" from sneaking into everyone's visibility.
        var implicitFoundational = new HashSet<Guid>(
            modules
                .Where(m => string.Equals(m.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase)
                            && FoundationalAppNames.Contains(m.Name))
                .Select(m => m.AppId));

        // All Microsoft-published modules in the release. Microsoft apps
        // (first-party) have unrestricted access to each other's symbols
        // in BC's compiler — the AL dev tools surface every Microsoft
        // codeunit / table / page without requiring an app.json dep.
        // Examples surfaced in the diagnostic samples:
        //   - `_Exclude_APIV1_` / `_Exclude_APIV2_` reference
        //     `Codeunit "O365 Setup Email"` which lives in another
        //     Microsoft app neither lists as a dep.
        //   - `Application Test Library` references `Library - Utility`
        //     and friends across sibling Microsoft test-library apps.
        //   - `Bank Account Reconciliation With AI Tests` references
        //     `Library - ERM`, `Library - Random`, `Assert`.
        // Third-party apps still respect declared deps + the foundational
        // set; this expansion only applies when the importing module is
        // Microsoft-published.
        var allMicrosoftAppIds = new HashSet<Guid>(
            modules
                .Where(m => string.Equals(m.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.AppId));

        // Transitive closure per module, including the module itself,
        // the implicit foundational AppIds, and the PlatformAppId sentinel
        // for the synthetic virtual-table entries. Microsoft-published
        // modules additionally see every other Microsoft module in the
        // release (see comment above on allMicrosoftAppIds).
        var result = new Dictionary<long, HashSet<Guid>>(modules.Count);
        foreach (var m in modules)
        {
            var visible = new HashSet<Guid>(implicitFoundational) { m.AppId, PlatformAppId };
            if (string.Equals(m.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase))
            {
                visible.UnionWith(allMicrosoftAppIds);
            }
            WalkDeps(m.AppId, visible, directDepsByAppId);
            result[m.Id] = visible;
        }
        return result;
    }

    /// <summary>
    /// Well-known Microsoft module names whose AppIds are implicitly
    /// visible to every other module in the release. These are the
    /// "platform" apps the AL compiler always resolves against without
    /// requiring an <c>app.json</c> dependency declaration.
    ///
    /// <para><b>EXTENDING:</b> when Microsoft introduces a new
    /// foundational umbrella app (every extension can reference it
    /// without declaring a dep), add its display name here. Matched
    /// case-insensitively against <c>OeModule.Name</c> + a
    /// Publisher = "Microsoft" filter so a third-party can't ship an
    /// app with the same name to widen visibility.</para>
    /// </summary>
    private static readonly HashSet<string> FoundationalAppNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Application",
        "Base Application",
        "Application",
        "Business Foundation",
    };

    /// <summary>
    /// Sentinel AppId for the synthetic platform virtual tables (and
    /// any other catalog entry that doesn't belong to a specific
    /// imported module). Every module's visibility set includes this
    /// AppId so chains through <c>Record Field</c>, <c>Record Company</c>
    /// etc. resolve cleanly. <see cref="Guid.Empty"/> as a sentinel is
    /// safe because real BC AppIds are always non-empty GUIDs.
    /// </summary>
    private static readonly Guid PlatformAppId = Guid.Empty;

    /// <summary>
    /// BC platform virtual tables — runtime-provided system tables
    /// every extension can reference but no module's symbol package
    /// declares. The id range <c>2000000001 – 2000000999</c> is
    /// reserved by Microsoft for these; names have been stable across
    /// BC versions (the compiler emits canonical names like
    /// <c>"Field"</c> / <c>"Company"</c> / <c>"User"</c> rather than
    /// the numeric id when these are referenced from AL source).
    ///
    /// Synthesised as catalog entries during Phase-2 so type lookups
    /// on <c>TempFieldSet: Record Field</c> and similar variable
    /// declarations succeed. Chain steps like <c>TempFieldSet.GET(...)</c>
    /// then resolve through <see cref="ALDevToolbox.Services.Al.AlBuiltinMethods.RecordMethods"/>;
    /// field-specific accesses (<c>TempFieldSet.TableNo</c>) still
    /// drop as <c>chain-step</c> unresolveds because we don't have the
    /// platform-table schemas — acceptable trade-off given the volume.
    ///
    /// <para><b>EXTENDING:</b> if a new BC version adds a platform
    /// virtual table — or renames an existing one — add an entry
    /// here. The numeric range safety net
    /// (<c>AlReferenceExtractor.IsPlatformVirtualTableId</c>) silences
    /// the diagnostic even for unlisted ids, but the named entry is
    /// what lets `Record &lt;Name&gt;` chains resolve cleanly through
    /// the synthetic catalog. Source for the canonical id → name map:
    /// hougaard.com (cited at the call site below).</para>
    /// </summary>
    private static readonly (int Id, string Name)[] PlatformVirtualTables =
    {
        // Source: https://www.hougaard.com/all-the-2-billion-tables-in-business-central-v16/
        // — authoritative enumeration of the BC virtual-table id space.
        // The IsPlatformVirtualTableId range check still catches any
        // numeric id we miss; this map is for named-type chain resolution
        // (`TempFieldSet: Record Field` etc.).
        (2000000001, "Object"),
        (2000000004, "Permission Set"),
        (2000000005, "Permission"),
        (2000000006, "Company"),
        (2000000007, "Date"),
        (2000000009, "Session"),
        (2000000020, "Drive"),
        (2000000022, "File"),
        (2000000026, "Integer"),
        (2000000028, "Table Information"),
        (2000000029, "System Object"),
        (2000000038, "AllObj"),
        (2000000039, "Printer"),
        (2000000040, "License Information"),
        (2000000041, "Field"),
        (2000000043, "License Permission"),
        (2000000044, "Permission Range"),
        (2000000045, "Windows Language"),
        (2000000048, "Database"),
        (2000000049, "Code Coverage"),
        (2000000053, "Access Control"),
        (2000000055, "SID - Account ID"),
        (2000000058, "AllObjWithCaption"),
        (2000000063, "Key"),
        (2000000065, "Send-To Program"),
        (2000000066, "Style Sheet"),
        (2000000067, "User Default Style Sheet"),
        (2000000068, "Record Link"),
        (2000000069, "Add-in"),
        (2000000071, "Object Metadata"),
        (2000000072, "Profile"),
        (2000000073, "User Personalization"),
        (2000000074, "Profile Metadata"),
        (2000000075, "User Metadata"),
        (2000000076, "Web Service"),
        (2000000078, "Chart"),
        (2000000080, "Page Data Personalization"),
        (2000000081, "Upgrade Blob Storage"),
        (2000000082, "Report Layout"),
        (2000000083, "Tenant Profile Setting"),
        (2000000084, "Tenant Profile Extension"),
        (2000000086, "Profile Configuration Symbols"),
        (2000000095, "API Webhook Subscription"),
        (2000000096, "API Webhook Notification"),
        (2000000097, "API Webhook Entity"),
        (2000000098, "API Webhook Notification Aggr"),
        (2000000103, "Debugger Watch Value"),
        (2000000107, "Isolated Storage"),
        (2000000110, "Active Session"),
        (2000000111, "Session Event"),
        (2000000112, "Server Instance"),
        (2000000114, "Document Service"),
        (2000000120, "User"),
        (2000000121, "User Property"),
        (2000000130, "Device"),
        (2000000135, "Table Synch. Setup"),
        (2000000136, "Table Metadata"),
        (2000000137, "CodeUnit Metadata"),
        (2000000138, "Page Metadata"),
        (2000000139, "Report Metadata"),
        (2000000140, "Event Subscription"),
        (2000000141, "Table Relations Metadata"),
        (2000000142, "Query Metadata"),
        (2000000143, "Page Action"),
        (2000000144, "Power BI Blob"),
        (2000000145, "Power BI Default Selection"),
        (2000000146, "Intelligent Cloud"),
        (2000000152, "NAV App Data Archive"),
        (2000000153, "NAV App Installed App"),
        (2000000154, "Database Locks"),
        (2000000157, "NAV App Extra"),
        (2000000159, "Data Sensitivity"),
        (2000000162, "NAV App Capabilities"),
        (2000000163, "NAV App Object Prerequisites"),
        (2000000164, "Time Zone"),
        (2000000165, "Tenant Permission Set"),
        (2000000166, "Tenant Permission"),
        (2000000167, "Aggregate Permission Set"),
        (2000000168, "Tenant Web Service"),
        (2000000169, "NAV App Tenant Add-In"),
        (2000000170, "Configuration Package File"),
        (2000000171, "Page Table Field"),
        (2000000172, "Table Field Types"),
        (2000000173, "Intelligent Cloud Status"),
        (2000000175, "Scheduled Task"),
        (2000000177, "Tenant Profile"),
        (2000000178, "All Profile"),
        (2000000179, "OData Edm Type"),
        (2000000180, "Media Set"),
        (2000000181, "Media"),
        (2000000182, "Media Resources"),
        (2000000183, "Tenant Media Set"),
        (2000000184, "Tenant Media"),
        (2000000185, "Tenant Media Thumbnails"),
        (2000000186, "Profile Page Metadata"),
        (2000000187, "Tenant Profile Page Metadata"),
        (2000000188, "User Page Metadata"),
        (2000000189, "Tenant License State"),
        (2000000190, "Entitlement Set"),
        (2000000191, "Entitlement"),
        (2000000192, "Page Control Field"),
        (2000000193, "Api Web Service"),
        (2000000194, "Webhook Notification"),
        (2000000195, "Membership Entitlement"),
        (2000000196, "Object Options"),
        (2000000197, "Token Cache"),
        (2000000198, "Page Documentation"),
        (2000000199, "Webhook Subscription"),
        (2000000200, "NAV App Tenant Operation"),
        (2000000201, "NAV App Setting"),
        (2000000202, "All Control Fields"),
        (2000000203, "Report Data Items"),
        (2000000204, "Page Info And Fields"),
        (2000000205, "Object Access Intent Override"),
        (2000000206, "Published Application"),
        (2000000207, "Application Object Metadata"),
        (2000000208, "Application Resource"),
        (2000000209, "Application Dependency"),
        (2000000210, "Tenant Feature Key"),
        (2000000211, "Feature Key"),
        (2000000212, "Installed Application"),
        (2000000213, "Designed Query"),
        (2000000214, "Designed Query Caption"),
        (2000000215, "Designed Query Category"),
        (2000000216, "Designed Query Column"),
        (2000000217, "Designed Query Column Filter"),
        (2000000218, "Designed Query Data Item"),
        (2000000219, "Designed Query Filter"),
        (2000000220, "Designed Query Join"),
        (2000000221, "Designed Query Order By"),
    };

    private static void WalkDeps(
        Guid current, HashSet<Guid> acc,
        Dictionary<Guid, HashSet<Guid>> directDepsByAppId)
    {
        if (!directDepsByAppId.TryGetValue(current, out var deps)) return;
        foreach (var dep in deps)
        {
            // Add returns false when already present — prevents infinite
            // recursion on (degenerate) cyclic dependency declarations.
            if (acc.Add(dep)) WalkDeps(dep, acc, directDepsByAppId);
        }
    }

    private static HashSet<Guid> ParseDependencyAppIds(string json)
    {
        var set = new HashSet<Guid>();
        if (string.IsNullOrEmpty(json) || json == "[]") return set;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return set;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("id", out var idProp)
                    && idProp.ValueKind == System.Text.Json.JsonValueKind.String
                    && Guid.TryParse(idProp.GetString(), out var id))
                {
                    set.Add(id);
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Malformed dep JSON shouldn't kill the import — the module
            // just won't contribute deps to its visibility set.
        }
        return set;
    }

    private sealed record MemberEntry(
        long SymbolId, string Name, string Kind, string? ReturnTypeKeyword, string? ReturnTypeName);

    private sealed record ExtensionEntry(Guid AppId, long ObjectId);

    /// <summary>
    /// Composite-key comparer for object-identity lookups
    /// <c>(AppId, Kind, Name)</c>. Kind and Name use ordinal-ignore-case
    /// (AL identifiers are case-insensitive); AppId is a Guid with its
    /// own structural equality. Storing identity rather than name alone
    /// disambiguates name collisions across kinds / modules — a Table
    /// and a TableExtension can both be named "Sales Header".
    /// </summary>
    private sealed class ObjectIdentityComparer : IEqualityComparer<(Guid AppId, string Kind, string Name)>
    {
        public bool Equals((Guid AppId, string Kind, string Name) x, (Guid AppId, string Kind, string Name) y) =>
            x.AppId == y.AppId
            && string.Equals(x.Kind, y.Kind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((Guid AppId, string Kind, string Name) obj) =>
            HashCode.Combine(
                obj.AppId,
                obj.Kind.ToLowerInvariant(),
                obj.Name.ToLowerInvariant());
    }

    /// <summary>
    /// IAlTypeResolver implementation backed by the in-memory catalogs
    /// built once per release. Dependency-aware: when constructed with
    /// a non-null <c>visibleAppIds</c> set, type and member lookups
    /// only return matches whose declaring module's AppId is in the
    /// caller's visibility set (the transitive closure of the caller
    /// module's app.json dependencies). When the visibility set is
    /// null, the resolver is permissive — used by tests that don't
    /// care about dependency direction.
    ///
    /// Member lookup also walks tableextensions / pageextensions
    /// targeting the receiver's base type: a procedure added by
    /// CustomerExt is callable as <c>Cust.MyMethod()</c> on a
    /// Customer-typed variable. The returned AlMember tags
    /// <see cref="ALDevToolbox.Services.Al.AlMember.DeclaringType"/>
    /// with the extension so the extractor stamps the reference's
    /// target at the extension (the actual declaration site), not the
    /// base table. Extensions are also filtered by visibility — if the
    /// caller's module doesn't depend on the extension's module, the
    /// extension's members are invisible.
    /// </summary>
    private sealed class CatalogResolver : ALDevToolbox.Services.Al.IAlTypeResolver
    {
        private readonly Dictionary<string, List<ALDevToolbox.Services.Al.AlTypeRef>> _typesByName;
        private readonly Dictionary<long, ALDevToolbox.Services.Al.AlTypeRef> _typesByObjectId;
        private readonly Dictionary<(Guid AppId, string Kind, string Name), long> _objectIdByIdentity;
        private readonly Dictionary<long, List<MemberEntry>> _members;
        private readonly Dictionary<string, List<ExtensionEntry>> _extensionsByBaseName;
        private readonly Dictionary<long, string> _sourceTablesByObjectId;
        private readonly HashSet<Guid>? _visibleAppIds;

        public CatalogResolver(
            Dictionary<string, List<ALDevToolbox.Services.Al.AlTypeRef>> typesByName,
            Dictionary<long, ALDevToolbox.Services.Al.AlTypeRef> typesByObjectId,
            Dictionary<(Guid AppId, string Kind, string Name), long> objectIdByIdentity,
            Dictionary<long, List<MemberEntry>> members,
            Dictionary<string, List<ExtensionEntry>> extensionsByBaseName,
            Dictionary<long, string> sourceTablesByObjectId,
            HashSet<Guid>? visibleAppIds)
        {
            _typesByName = typesByName;
            _typesByObjectId = typesByObjectId;
            _objectIdByIdentity = objectIdByIdentity;
            _members = members;
            _extensionsByBaseName = extensionsByBaseName;
            _sourceTablesByObjectId = sourceTablesByObjectId;
            _visibleAppIds = visibleAppIds;
        }

        /// <summary>
        /// Resolves a name to a single AlTypeRef. When multiple objects
        /// share the name (e.g. a Table and a TableExtension both named
        /// "Sales Header" — Microsoft's Subscription Billing app does
        /// this against Base Application's Sales Header), preference is:
        /// <list type="number">
        ///   <item>Visible (the caller's module can see the declaring
        ///         AppId via app.json dependencies).</item>
        ///   <item>Kind matches the caller's hint (<paramref name="expectedKeyword"/>).
        ///         A page's <c>SourceTable = "Sales Header"</c> arrives
        ///         here with keyword <c>Record</c> — a TableExtension
        ///         named "Sales Header" can't be a page's source table,
        ///         only the base Table can.</item>
        ///   <item>Otherwise, a non-extension kind beats an extension
        ///         kind. Typed-literal references in AL almost always
        ///         mean the base object.</item>
        /// </list>
        /// </summary>
        public ALDevToolbox.Services.Al.AlTypeRef? ResolveTypeByName(string typeName, string? expectedKeyword = null)
        {
            if (!_typesByName.TryGetValue(typeName, out var candidates)) return null;
            var expectedKind = MapKeywordToKind(expectedKeyword);
            ALDevToolbox.Services.Al.AlTypeRef? best = null;
            foreach (var t in candidates)
            {
                if (!IsVisible(t.AppId)) continue;
                // With a kind hint, exact match wins outright — return
                // the first exact-kind visible candidate.
                if (expectedKind is not null
                    && string.Equals(t.Kind, expectedKind, StringComparison.OrdinalIgnoreCase))
                {
                    return t;
                }
                if (best is null) { best = t; continue; }
                if (IsExtensionKind(best.Kind) && !IsExtensionKind(t.Kind))
                {
                    best = t;
                }
            }
            return best;
        }

        /// <summary>
        /// Maps the caller's kind hint to a catalog kind. Accepts both
        /// AL type keywords (<c>Record</c>, <c>Codeunit</c>, <c>Page</c>, …)
        /// and catalog kind values (<c>table</c>, <c>codeunit</c>,
        /// <c>pageextension</c>, …) — the latter for cases like
        /// <c>OwnerType()</c> in the extractor where we already have the
        /// owner's catalog kind and want bare self-calls on a
        /// pageextension named the same as its base page to land on the
        /// extension, not on the base.
        /// <c>Record</c> is the only keyword that doesn't passthrough —
        /// it maps to <c>table</c>. The rest are identical except for
        /// casing.
        /// </summary>
        private static string? MapKeywordToKind(string? keyword)
        {
            if (string.IsNullOrEmpty(keyword)) return null;
            var lower = keyword.ToLowerInvariant();
            if (lower == "record") return "table";
            return lower;
        }

        public ALDevToolbox.Services.Al.AlMember? ResolveMember(
            ALDevToolbox.Services.Al.AlTypeRef owner, string memberName)
        {
            // Owner's own members win — same-name members on extensions
            // are shadowed by the base's own declaration (AL dispatch).
            // Use the composite identity key so a same-named extension
            // doesn't get confused with the base when looking up the
            // owner's DB id.
            if (_objectIdByIdentity.TryGetValue((owner.AppId, owner.Kind, owner.Name), out var ownerId)
                && _members.TryGetValue(ownerId, out var ownerMembers))
            {
                var match = ownerMembers.FirstOrDefault(m =>
                    string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return new ALDevToolbox.Services.Al.AlMember(
                        Name: match.Name,
                        Kind: match.Kind,
                        ReturnTypeKeyword: match.ReturnTypeKeyword,
                        ReturnTypeName: match.ReturnTypeName);
                }
            }

            // Walk visible extensions of this base.
            if (_extensionsByBaseName.TryGetValue(owner.Name, out var extensions))
            {
                foreach (var ext in extensions)
                {
                    if (!IsVisible(ext.AppId)) continue;
                    if (!_members.TryGetValue(ext.ObjectId, out var extMembers)) continue;
                    var match = extMembers.FirstOrDefault(m =>
                        string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase));
                    if (match is null) continue;

                    _typesByObjectId.TryGetValue(ext.ObjectId, out var declaringType);
                    return new ALDevToolbox.Services.Al.AlMember(
                        Name: match.Name,
                        Kind: match.Kind,
                        ReturnTypeKeyword: match.ReturnTypeKeyword,
                        ReturnTypeName: match.ReturnTypeName,
                        DeclaringType: declaringType);
                }
            }

            return null;
        }

        public string? ResolveSourceTableName(ALDevToolbox.Services.Al.AlTypeRef target)
        {
            if (_objectIdByIdentity.TryGetValue((target.AppId, target.Kind, target.Name), out var dbId)
                && _sourceTablesByObjectId.TryGetValue(dbId, out var source))
            {
                return source;
            }
            return null;
        }

        private bool IsVisible(Guid appId) =>
            _visibleAppIds is null || _visibleAppIds.Contains(appId);

        private static bool IsExtensionKind(string kind) =>
            kind.EndsWith("extension", StringComparison.OrdinalIgnoreCase);
    }
}
