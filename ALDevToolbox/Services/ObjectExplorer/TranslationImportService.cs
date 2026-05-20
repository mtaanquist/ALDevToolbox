using System.IO.Compression;
using System.Xml;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;
using OeModule = ALDevToolbox.Domain.Entities.ObjectExplorer.Module;
using OeModuleSymbol = ALDevToolbox.Domain.Entities.ObjectExplorer.ModuleSymbol;
using OeModuleTranslation = ALDevToolbox.Domain.Entities.ObjectExplorer.ModuleTranslation;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Reads XLIFF translation files and persists them as
/// <see cref="OeModuleTranslation"/> rows. Three entry points (single-file
/// admin upload, per-release ZIP admin upload, automatic extraction during
/// <see cref="ReleaseImportService"/>) funnel into a shared
/// <c>(module, language)</c> clobber + insert + best-effort symbol-resolution
/// path. See GitHub issue #151 for the larger feature and
/// <c>CLAUDE.md</c> for the architecture fences this service stays inside.
/// </summary>
public class TranslationImportService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<TranslationImportService> _logger;

    public TranslationImportService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ILogger<TranslationImportService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; TranslationImportService called outside an authenticated request.");

    // ── Single .xlf admin upload ───────────────────────────────────────

    /// <summary>
    /// Admin uploads one <c>.xlf</c> against a known module. Re-uploading
    /// for the same target language clobbers the previous rows.
    /// </summary>
    public async Task<TranslationImportSummary> ImportSingleAsync(
        int releaseId,
        long moduleId,
        Stream xliff,
        string fileName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(xliff);
        var orgId = RequireOrganizationId();

        var module = await _db.OeModules.AsNoTracking()
            .Where(m => m.Id == moduleId && m.ReleaseId == releaseId)
            .Select(m => new { m.Id, m.Name })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (module is null)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["ModuleId"] = $"Module {moduleId} does not belong to release {releaseId}.",
            });
        }

        // Diagnostic baseline: working-set before parse + the upload's
        // declared length (when seekable) so operators can correlate
        // memory growth against XLIFF size in the logs (issue #207).
        var workingSetBefore = Environment.WorkingSet;
        var xliffLength = xliff.CanSeek ? xliff.Length : (long?)null;
        _logger.LogInformation(
            "XLIFF upload starting: File={File} ModuleId={ModuleId} XliffBytes={XliffBytes} WorkingSetBefore={WorkingSetBefore}",
            fileName, moduleId, xliffLength, workingSetBefore);

        int inserted;
        string targetLanguage;
        string? originalName;
        try
        {
            (inserted, targetLanguage, originalName) =
                await StreamParseAndReplaceAsync(orgId, moduleId, xliff, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["XliffFile"] = $"Could not parse \"{fileName}\" as XLIFF v1.2: {ex.Message}",
            });
        }

        var workingSetAfter = Environment.WorkingSet;
        _logger.LogInformation(
            "XLIFF upload finished: File={File} ModuleId={ModuleId} Lang={Lang} Inserted={Inserted} WorkingSetAfter={WorkingSetAfter} WorkingSetDelta={WorkingSetDelta}",
            fileName, moduleId, targetLanguage, inserted, workingSetAfter, workingSetAfter - workingSetBefore);

        // Light sanity check: the file's <file original> should match the
        // module name. Mismatch isn't fatal (admin may know better; we
        // chose the module explicitly) but it's worth logging.
        if (!string.IsNullOrEmpty(originalName)
            && !string.Equals(originalName, module.Name, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Translation upload {File} declares original=\"{Original}\" but admin attached it to module \"{Module}\" (id={ModuleId}).",
                fileName, originalName, module.Name, moduleId);
        }

        return new TranslationImportSummary(
            LanguageCode: targetLanguage,
            ModuleName: module.Name,
            Inserted: inserted);
    }

    // ── Per-release ZIP admin upload ───────────────────────────────────

    /// <summary>
    /// Admin uploads a ZIP holding one or more <c>.xlf</c> files. Each
    /// entry is matched to a module in the release by the XLIFF's
    /// <c>&lt;file original="..."&gt;</c> attribute → <see cref="OeModule.Name"/>
    /// (case-insensitive). Unmatched entries are skipped — their names
    /// appear in <see cref="TranslationZipImportSummary.UnmatchedFiles"/>
    /// so the admin can see why a file didn't land.
    /// </summary>
    public async Task<TranslationZipImportSummary> ImportZipAsync(
        int releaseId,
        Stream zip,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(zip);
        var orgId = RequireOrganizationId();

        var release = await _db.OeReleases.AsNoTracking()
            .Where(r => r.Id == releaseId)
            .Select(r => new { r.Id })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (release is null)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["ReleaseId"] = $"Release {releaseId} does not exist in this organisation.",
            });
        }

        // Build a case-insensitive Name → ModuleId map once per upload.
        var modules = await _db.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == releaseId)
            .Select(m => new { m.Id, m.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        var byName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in modules)
        {
            // Duplicate module names in one release shouldn't happen, but
            // if they do, last-write-wins is fine for this lookup.
            byName[m.Name] = m.Id;
        }

        int matched = 0, totalInserted = 0;
        var unmatched = new List<string>();
        var matchedSummaries = new List<TranslationImportSummary>();

        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (!entry.FullName.EndsWith(".xlf", StringComparison.OrdinalIgnoreCase)) continue;

            // Two-pass per entry: peek the header (cheap — reads only up
            // to the first <body>) to match the entry to a module, then
            // re-open and stream-parse for the import. The peek cost is
            // bounded by the <file> tag size (a few hundred bytes); the
            // alternative — staging the whole entry into memory to read
            // the header then re-parse — is exactly the shape #207
            // wants us to avoid.
            string? originalName;
            try
            {
                using var peekStream = entry.Open();
                originalName = PeekOriginal(peekStream);
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
            {
                _logger.LogWarning(ex, "Skipping XLIFF zip entry {Entry}: header parse failed.", entry.FullName);
                unmatched.Add(entry.FullName);
                continue;
            }

            if (string.IsNullOrEmpty(originalName)
                || !byName.TryGetValue(originalName, out var moduleId))
            {
                unmatched.Add(entry.FullName);
                continue;
            }

            var workingSetBefore = Environment.WorkingSet;
            _logger.LogInformation(
                "XLIFF zip entry starting: Entry={Entry} ModuleId={ModuleId} EntryBytes={EntryBytes} WorkingSetBefore={WorkingSetBefore}",
                entry.FullName, moduleId, entry.Length, workingSetBefore);

            int inserted;
            string targetLanguage;
            try
            {
                using var stream = entry.Open();
                (inserted, targetLanguage, _) =
                    await StreamParseAndReplaceAsync(orgId, moduleId, stream, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
            {
                _logger.LogWarning(ex, "Skipping XLIFF zip entry {Entry}: parse failed.", entry.FullName);
                unmatched.Add(entry.FullName);
                continue;
            }

            var workingSetAfter = Environment.WorkingSet;
            _logger.LogInformation(
                "XLIFF zip entry finished: Entry={Entry} ModuleId={ModuleId} Lang={Lang} Inserted={Inserted} WorkingSetAfter={WorkingSetAfter} WorkingSetDelta={WorkingSetDelta}",
                entry.FullName, moduleId, targetLanguage, inserted, workingSetAfter, workingSetAfter - workingSetBefore);

            matched++;
            totalInserted += inserted;
            matchedSummaries.Add(new TranslationImportSummary(
                LanguageCode: targetLanguage,
                ModuleName: originalName!,
                Inserted: inserted));
        }

        return new TranslationZipImportSummary(
            MatchedFiles: matched,
            UnmatchedFiles: unmatched,
            TotalInserted: totalInserted,
            PerFile: matchedSummaries);
    }

    // ── Shared parse → clobber → insert → resolve ──────────────────────

    /// <summary>
    /// Streams an XLIFF stream end-to-end: reads <c>&lt;file&gt;</c>
    /// metadata, deletes existing rows for <c>(moduleId, target-language)</c>,
    /// and inserts each trans-unit as it's parsed. Peak heap stays bounded
    /// to a single 500-row insert buffer plus the per-unit DOM held by
    /// <see cref="AlXliffParser.ParseStreaming"/> — see issue #207 for the
    /// shape we replaced. Returns <c>(insertedCount, targetLanguage, originalName)</c>.
    /// </summary>
    private async Task<(int Inserted, string TargetLanguage, string? OriginalName)> StreamParseAndReplaceAsync(
        int orgId, long moduleId, Stream xliff, CancellationToken ct)
    {
        // The XLIFF header is read first by ParseStreaming, but per-unit
        // callbacks run inside that same call — we need the symbol map,
        // the chunk buffer, and the clobber side-effect prepared in
        // advance. Read the header without trans-unit callbacks first by
        // staging a peek; once we know the target language we can run the
        // DELETE and load the symbol map, then stream-parse for real.
        //
        // Two passes are unavoidable because the clobber key (target
        // language) lives in the file header and the import is
        // semantically "replace this (module, language) wholesale". We
        // keep the peek scope tight: only the <file> attributes are read
        // before the stream is closed.
        if (!xliff.CanSeek)
        {
            // Stage to a temp file so we can read the header, then re-open
            // for the streaming walk. Temp file beats MemoryStream for
            // multi-hundred-MB XLIFFs — that's exactly the case #207
            // surfaced.
            var tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await xliff.CopyToAsync(fs, ct).ConfigureAwait(false);
                }
                await using var reopen = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.None);
                return await DoStreamParseAndReplaceAsync(orgId, moduleId, reopen, ct).ConfigureAwait(false);
            }
            finally
            {
                try { File.Delete(tempPath); }
                catch (IOException ex) { _logger.LogWarning(ex, "Failed to delete temp XLIFF file {Path}.", tempPath); }
            }
        }

        return await DoStreamParseAndReplaceAsync(orgId, moduleId, xliff, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Inner implementation of <see cref="StreamParseAndReplaceAsync"/>
    /// against a seekable stream. Reads the header to learn the target
    /// language, runs the DELETE, loads the symbol map, then rewinds and
    /// streams trans-units into the insert buffer.
    /// </summary>
    private async Task<(int Inserted, string TargetLanguage, string? OriginalName)> DoStreamParseAndReplaceAsync(
        int orgId, long moduleId, Stream xliff, CancellationToken ct)
    {
        // Pass 1: header only (no per-unit callback work). The peek hands
        // back as soon as it's seen <file>; the stream advances forward,
        // so we rewind before the real walk.
        var headerOnly = AlXliffParser.ParseStreaming(xliff, _ => { /* discard */ }, ct);

        // Skip XLIFFs whose source-language matches their target-language.
        // That's the shape the AL compiler emits as <Module>.g.xlf — the
        // generator template where every <target> mirrors the <source>
        // verbatim. Ingesting it would double every search hit with an
        // English no-op row and put a phantom en-US entry on the
        // per-release languages chip. Doing the check here means all
        // intake paths (single-file admin, ZIP) catch it without each
        // having to know the filename convention.
        if (!string.IsNullOrEmpty(headerOnly.SourceLanguage)
            && string.Equals(headerOnly.SourceLanguage, headerOnly.TargetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Skipped XLIFF for module {ModuleId}: source-language and target-language are both {Lang} (generator template, not a real translation).",
                moduleId, headerOnly.TargetLanguage);
            return (0, headerOnly.TargetLanguage, headerOnly.OriginalName);
        }

        // Symbol map keyed by lower(name) → list of (id, kind, objectId).
        // Loaded once per upload so the per-row resolver doesn't run a
        // query for each of the thousands of trans-units. Field captions
        // and tooltips resolve to kind='table_field' / 'page_field';
        // labels (sub_kind=namedtype) resolve to a same-name symbol —
        // most commonly a Label declaration emitted by the source
        // extractor. Page controls and enum values (issue #151 v2)
        // resolve via the new page_control / enum_value kinds the
        // extractor emits.
        var symbols = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.ModuleId == moduleId)
            .Select(s => new { s.Id, s.Name, s.Kind, s.ObjectId })
            .ToListAsync(ct).ConfigureAwait(false);
        var symbolByName = new Dictionary<string, List<(long Id, string Kind, long ObjectId)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in symbols)
        {
            if (!symbolByName.TryGetValue(s.Name, out var list))
            {
                list = new List<(long, string, long)>();
                symbolByName[s.Name] = list;
            }
            list.Add((s.Id, s.Kind, s.ObjectId));
        }

        // Object-name → ObjectId map, scoped by object kind. Lets the
        // resolver scope page-control / enum-value matches to the right
        // owning object when multiple objects in the module share a
        // sub-element name (e.g. "Description" exists as a page_control
        // on many pages).
        var objects = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.ModuleId == moduleId)
            .Select(o => new { o.Id, o.Name, o.Kind })
            .ToListAsync(ct).ConfigureAwait(false);
        var objectByNameAndKind = new Dictionary<(string Name, string Kind), long>(
            new ObjectKeyComparer());
        foreach (var o in objects)
        {
            objectByNameAndKind[(o.Name, o.Kind)] = o.Id;
        }

        // Clobber existing rows for (module, language) so re-upload
        // replaces wholesale rather than merging. Executed as one DELETE
        // for tracker hygiene; the unique index on (module, language,
        // trans_unit_id) makes the insert side safe even when XLIFFs
        // accidentally double up a trans-unit.
        await _db.OeModuleTranslations
            .Where(t => t.ModuleId == moduleId && t.LanguageCode == headerOnly.TargetLanguage)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        // Rewind for pass 2.
        xliff.Seek(0, SeekOrigin.Begin);

        int inserted = 0;
        int pending = 0;
        const int chunkSize = 500;
        var now = DateTime.UtcNow;
        // XLIFFs occasionally repeat the same id (BC compiler bug history;
        // hand-edited files); dedupe inside the upload so we don't violate
        // the unique index. Last-write-wins on collisions.
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        // ParseStreaming's pull-loop is synchronous (XmlReader has async
        // counterparts, but the per-unit DOM build uses XElement.Load
        // which doesn't). Flushing mid-stream therefore needs sync EF —
        // SaveChanges, not SaveChangesAsync — so we don't block-await an
        // outer task inside the callback. Npgsql exposes a real sync
        // path; using it here is safe and avoids the sync-over-async
        // deadlock shape.
        var header = AlXliffParser.ParseStreaming(xliff, unit =>
        {
            if (!seenIds.Add(unit.Id)) return;
            var kind = AlXliffParser.BucketKind(unit.Hint);
            long? symbolId = ResolveSymbolId(symbolByName, objectByNameAndKind, unit.Hint, kind);

            _db.OeModuleTranslations.Add(new OeModuleTranslation
            {
                OrganizationId = orgId,
                ModuleId = moduleId,
                LanguageCode = headerOnly.TargetLanguage,
                TransUnitId = unit.Id,
                SourceText = unit.SourceText,
                TargetText = unit.TargetText,
                TargetState = unit.TargetState,
                Kind = kind,
                ObjectKind = unit.Hint?.ObjectKind,
                ObjectName = unit.Hint?.ObjectName,
                SubKind = unit.Hint?.SubKind,
                SubName = unit.Hint?.SubName,
                PropertyName = unit.Hint?.PropertyName,
                DeveloperNote = unit.DeveloperNote,
                SymbolId = symbolId,
                CreatedAt = now,
            });
            inserted++;
            pending++;
            if (pending >= chunkSize)
            {
                _db.SaveChanges();
                _db.ChangeTracker.Clear();
                pending = 0;
            }
        }, ct);

        // Drain any rows left in the tracker after the last partial
        // chunk. SaveChangesAsync here because we're back outside the
        // sync pull-loop.
        if (pending > 0)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
        }

        _logger.LogInformation(
            "Imported XLIFF translations: Module={ModuleId} Lang={Lang} Inserted={Inserted}",
            moduleId, header.TargetLanguage, inserted);
        return (inserted, header.TargetLanguage, header.OriginalName);
    }

    /// <summary>
    /// Reads only the <c>&lt;file original&gt;</c> attribute from an XLIFF
    /// stream without materialising any trans-units. Used by
    /// <see cref="ImportZipAsync"/> to match a zip entry to a module
    /// before deciding whether to commit to the full streaming parse.
    /// Returns <c>null</c> when the attribute is missing.
    /// </summary>
    private static string? PeekOriginal(Stream xliff)
    {
        var settings = new XmlReaderSettings
        {
            IgnoreWhitespace = true,
            IgnoreComments = true,
            DtdProcessing = DtdProcessing.Prohibit,
            CloseInput = false,
            Async = false,
        };
        using var reader = XmlReader.Create(xliff, settings);
        if (!reader.ReadToFollowing("file", "urn:oasis:names:tc:xliff:document:1.2"))
        {
            throw new InvalidDataException("XLIFF document is missing the <file> element.");
        }
        return reader.GetAttribute("original");
    }

    private sealed class ObjectKeyComparer : IEqualityComparer<(string Name, string Kind)>
    {
        public bool Equals((string Name, string Kind) x, (string Name, string Kind) y)
            => string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Kind, y.Kind, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Name, string Kind) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Kind));
    }

    /// <summary>
    /// Best-effort match from the parsed hint to a row in
    /// <c>oe_module_symbols</c>. Field captions / tooltips resolve to the
    /// field symbol; labels (NamedType sub-kind) to the label symbol;
    /// page controls / enum values (issue #151 v2) to the matching
    /// <c>page_control</c> / <c>enum_value</c> rows, scoped to the
    /// owning object when the hint carries an object name.
    /// </summary>
    private static long? ResolveSymbolId(
        IReadOnlyDictionary<string, List<(long Id, string Kind, long ObjectId)>> symbolByName,
        IReadOnlyDictionary<(string Name, string Kind), long> objectByNameAndKind,
        XliffLookupHint? hint,
        string bucketKind)
    {
        if (hint is null) return null;

        // Field caption / tooltip → match by field name (sub_name) against
        // table_field / page_field rows.
        if (string.Equals(hint.SubKind, "field", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(hint.SubName)
            && symbolByName.TryGetValue(hint.SubName, out var fieldCandidates))
        {
            var hit = ScopeToOwner(fieldCandidates, objectByNameAndKind, hint, "table_field", "page_field");
            if (hit is not null) return hit;
        }

        // Page control → match by control name against page_field +
        // page_control rows. Microsoft's LookupHint format names every
        // page-layout member "Control" regardless of whether the AL
        // source uses `field(...)`, `group(...)`, `repeater(...)`,
        // `cuegroup(...)` or `part(...)`. `field(...)` controls already
        // appear as `page_field` from the source extractor; the others
        // appear as the new `page_control` kind (issue #151 v2).
        if (string.Equals(hint.SubKind, "control", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(hint.SubName)
            && symbolByName.TryGetValue(hint.SubName, out var controlCandidates))
        {
            var hit = ScopeToOwner(controlCandidates, objectByNameAndKind, hint, "page_control", "page_field");
            if (hit is not null) return hit;
        }

        // Enum value → match the new `enum_value` rows (issue #151 v2).
        // Scoped to the owning enum / enumextension when the hint
        // names the parent object, so multi-enum modules with shared
        // value names resolve to the right declaration.
        if (string.Equals(hint.SubKind, "value", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(hint.SubName)
            && symbolByName.TryGetValue(hint.SubName, out var valueCandidates))
        {
            var hit = ScopeToOwner(valueCandidates, objectByNameAndKind, hint, "enum_value");
            if (hit is not null) return hit;
        }

        // Label (NamedType) → match by sub_name against any symbol with
        // that name. The source extractor doesn't yet emit a distinct
        // 'label' kind, but Microsoft labels typically live in codeunits
        // as local variables; this falls through to symbol-id=null for
        // most v1 cases and is fine.
        if (bucketKind == "label" && !string.IsNullOrEmpty(hint.SubName)
            && symbolByName.TryGetValue(hint.SubName, out var labelCandidates))
        {
            return labelCandidates[0].Id;
        }

        // Procedure-typed XLIFF entries are uncommon but possible (Method
        // sub-kind). Resolve to the first procedure-shaped row by name.
        if (string.Equals(hint.SubKind, "method", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(hint.SubName)
            && symbolByName.TryGetValue(hint.SubName, out var procCandidates))
        {
            var hit = procCandidates.FirstOrDefault(c =>
                c.Kind is "procedure" or "local_procedure" or "internal_procedure" or "protected_procedure");
            if (hit.Id != 0) return hit.Id;
        }

        return null;
    }

    /// <summary>
    /// Filters <paramref name="candidates"/> to those whose Kind is in
    /// <paramref name="kindAllowList"/>, optionally scoping to the
    /// ObjectId of the hint's named owner. When the owner can't be
    /// resolved (older XLIFFs without LookupHint, partner apps with
    /// drifted names) the first kind-matching candidate wins. Returns
    /// the symbol id of the winner, or null when nothing matches.
    /// </summary>
    private static long? ScopeToOwner(
        List<(long Id, string Kind, long ObjectId)> candidates,
        IReadOnlyDictionary<(string Name, string Kind), long> objectByNameAndKind,
        XliffLookupHint hint,
        params string[] kindAllowList)
    {
        long? ownerObjectId = null;
        if (!string.IsNullOrEmpty(hint.ObjectName) && !string.IsNullOrEmpty(hint.ObjectKind)
            && objectByNameAndKind.TryGetValue((hint.ObjectName, hint.ObjectKind), out var ownerId))
        {
            ownerObjectId = ownerId;
        }

        // Owner-scoped first pass: same object id + kind in the allow-list.
        if (ownerObjectId is { } scopeId)
        {
            foreach (var c in candidates)
            {
                if (c.ObjectId != scopeId) continue;
                foreach (var k in kindAllowList)
                {
                    if (string.Equals(c.Kind, k, StringComparison.OrdinalIgnoreCase))
                    {
                        return c.Id;
                    }
                }
            }
        }

        // Owner-less fallback: first candidate with a matching kind.
        foreach (var c in candidates)
        {
            foreach (var k in kindAllowList)
            {
                if (string.Equals(c.Kind, k, StringComparison.OrdinalIgnoreCase))
                {
                    return c.Id;
                }
            }
        }

        return null;
    }
}

/// <summary>
/// Result of importing one XLIFF file into one module.
/// </summary>
public sealed record TranslationImportSummary(
    string LanguageCode,
    string ModuleName,
    int Inserted);

/// <summary>
/// Result of importing a per-release ZIP. <see cref="UnmatchedFiles"/>
/// holds the entry names whose <c>&lt;file original&gt;</c> didn't match
/// any module in the release, plus any entries we couldn't parse.
/// </summary>
public sealed record TranslationZipImportSummary(
    int MatchedFiles,
    IReadOnlyList<string> UnmatchedFiles,
    int TotalInserted,
    IReadOnlyList<TranslationImportSummary> PerFile);
