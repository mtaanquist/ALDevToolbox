using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Read-only query API over the <c>oe_*</c> schema: the release / module /
/// object browse surfaces, the forward-edge MCP lookups, the object outline,
/// and procedure source/calls. The outline enriches interface objects with
/// their implementers via <see cref="ReferenceQueryService"/>.
///
/// The other read surfaces have their own focused services:
/// <see cref="ReferenceQueryService"/> (find-references / dependencies /
/// interface implementers), <see cref="SourceViewerService"/> (source-file
/// content / outline / navigation), <see cref="ObjectSearchService"/>
/// (cross-module search), <see cref="ReleaseComparisonService"/> (release
/// diffs), and <see cref="TranslationQueryService"/> (translations).
///
/// All methods are <c>AsNoTracking</c> and respect the tenant query filter
/// on <see cref="AppDbContext"/>.
/// </summary>
public class ObjectExplorerService
{
    private readonly AppDbContext _db;
    private readonly ReferenceQueryService _references;
    private readonly ILogger<ObjectExplorerService> _logger;

    public ObjectExplorerService(AppDbContext db, ReferenceQueryService references, ILogger<ObjectExplorerService> logger)
    {
        _db = db;
        _references = references;
        _logger = logger;
    }

    // ── Releases ────────────────────────────────────────────────────────

    /// <summary>
    /// Lists active Releases visible to the current org. Failed and
    /// in-progress Releases come along — the picker UI badges them
    /// distinctly but admins still need to see them.
    /// </summary>
    public async Task<List<ReleaseListItem>> ListReleasesAsync(
        bool includeSoftDeleted = false, CancellationToken ct = default)
    {
        var q = _db.OeReleases.AsNoTracking().AsQueryable();
        if (!includeSoftDeleted)
        {
            q = q.Where(r => r.DeletedAt == null);
        }
        var rows = await q
            .Select(r => new ReleaseListItem(
                r.Id, r.Label, r.Kind, r.Status, r.BcVersion, r.ParentReleaseId,
                ParentLabel: r.ParentRelease != null ? r.ParentRelease.Label : null,
                Publisher: r.Publisher,
                CustomerName: r.CustomerName,
                ImportedAt: r.ImportedAt,
                // Denormalised counters stamped at ingest time. The Releases
                // picker on a busy org used to spend most of its load budget
                // here — a correlated subquery summing LENGTH(content) over
                // multi-thousand-row file tables. ReleaseImportService now
                // pins these once when the Release flips to ready.
                SourceFileCount: r.SourceFileCount,
                SourceContentLength: r.SourceContentLength,
                DeletedAt: r.DeletedAt))
            .ToListAsync(ct);

        // Sort in memory: active rows first, then by BC version descending
        // (so "28.10" sorts above "28.2"), then by ImportedAt descending so
        // a re-import of the same version wins. The list is bounded — a
        // production org caps out at a few dozen releases — so the
        // in-memory sort cost is negligible compared to the row I/O.
        rows.Sort((a, b) =>
        {
            var deletedCmp = (a.DeletedAt == null ? 0 : 1).CompareTo(b.DeletedAt == null ? 0 : 1);
            if (deletedCmp != 0) return deletedCmp;
            var versionCmp = BcVersionComparer.Instance.Compare(b.BcVersion, a.BcVersion);
            if (versionCmp != 0) return versionCmp;
            return b.ImportedAt.CompareTo(a.ImportedAt);
        });
        return rows;
    }

    /// <summary>
    /// Returns the Release header plus a denormalised module count for the
    /// page title.
    /// </summary>
    public async Task<ReleaseDetail?> GetReleaseAsync(int releaseId, CancellationToken ct = default)
    {
        var row = await _db.OeReleases.AsNoTracking()
            .Where(r => r.Id == releaseId)
            .Select(r => new
            {
                r.Id, r.Label, r.Kind, r.Status, r.BcVersion, r.ParentReleaseId, r.ImportedAt,
                ParentLabel = r.ParentRelease != null ? r.ParentRelease.Label : null,
            })
            .SingleOrDefaultAsync(ct);
        if (row is null) return null;

        var moduleCount = await _db.OeModules.AsNoTracking()
            .CountAsync(m => m.ReleaseId == releaseId, ct);

        return new ReleaseDetail(
            Id: row.Id,
            Label: row.Label,
            Kind: row.Kind,
            Status: row.Status,
            BcVersion: row.BcVersion,
            ParentReleaseId: row.ParentReleaseId,
            ParentLabel: row.ParentLabel,
            ImportedAt: row.ImportedAt,
            ModuleCount: moduleCount);
    }

    // ── Modules ─────────────────────────────────────────────────────────

    /// <summary>
    /// Lists modules in a Release. Test apps / internal apps / language packs
    /// are filtered out by default — admins can flip them in via the filter
    /// toggles.
    /// </summary>
    public async Task<List<ModuleListItem>> ListModulesAsync(int releaseId, ModuleListFilter filter, CancellationToken ct = default)
    {
        var q = _db.OeModules.AsNoTracking().Where(m => m.ReleaseId == releaseId);
        if (!filter.IncludeTest) q = q.Where(m => !m.IsTest);
        if (!filter.IncludeInternal) q = q.Where(m => !m.IsInternal);
        if (!filter.IncludeLanguagePack) q = q.Where(m => !m.IsLanguagePack);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim().ToLower();
            q = q.Where(m => m.Name.ToLower().Contains(s) || m.Publisher.ToLower().Contains(s));
        }

        return await q.OrderBy(m => m.Publisher).ThenBy(m => m.Name)
            .Select(m => new ModuleListItem(
                m.Id, m.AppId, m.Name, m.Publisher, m.Version, m.Target,
                m.IsTest, m.IsInternal, m.IsLanguagePack,
                m.Objects.Count))
            .ToListAsync(ct);
    }

    // ── Objects ─────────────────────────────────────────────────────────

    /// <summary>
    /// Paginated object list within a module — feeds the object browser table.
    /// </summary>
    public async Task<ObjectListPage> ListObjectsAsync(
        long moduleId, ObjectListFilter filter, int skip, int take, CancellationToken ct = default)
    {
        var q = _db.OeModuleObjects.AsNoTracking().Where(o => o.ModuleId == moduleId);

        var kinds = ObjectSearchRanking.NormalizeKinds(filter.Kinds);
        if (kinds is { Count: > 0 })
        {
            q = q.Where(o => kinds.Contains(o.Kind));
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            var lower = s.ToLower();
            // Numeric search matches the object id; substring otherwise. The
            // search box accepts either, mirroring the convention from the
            // earlier base-app browser the new schema replaces.
            if (int.TryParse(s, out var asInt))
            {
                q = q.Where(o => o.ObjectId == asInt || o.Name.ToLower().Contains(lower));
            }
            else
            {
                q = q.Where(o => o.Name.ToLower().Contains(lower));
            }
        }

        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(o => o.Kind).ThenBy(o => o.Name)
            .Skip(skip).Take(take)
            .Select(o => new ObjectListItem(
                o.Id, o.Kind, o.ObjectId, o.Name, o.Namespace,
                o.ExtendsAppId, o.ExtendsObjectName,
                o.SourceFileId, o.LineNumber))
            .ToListAsync(ct);
        return new ObjectListPage(rows, total);
    }

    /// <summary>
    /// Returns one object's full detail: module context, source file pointer,
    /// inline symbol and variable lists. The inspector panel uses this so it
    /// doesn't paginate within the object — these lists are bounded by the
    /// object's own structure.
    /// </summary>
    public async Task<ObjectDetail?> GetObjectAsync(long objectId, CancellationToken ct = default)
    {
        var header = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Id == objectId)
            .Select(o => new
            {
                o.Id, o.Kind, o.ObjectId, o.Name, o.Namespace, o.ModuleId,
                ModuleName = o.Module!.Name,
                o.ExtendsAppId, o.ExtendsObjectName,
                o.SourceFileId,
                SourceFilePath = o.SourceFile != null ? o.SourceFile.Path : null,
                o.LineNumber,
            })
            .SingleOrDefaultAsync(ct);
        if (header is null) return null;

        var symbols = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.ObjectId == objectId)
            .OrderBy(s => s.Kind).ThenBy(s => s.Name)
            .Select(s => new ObjectSymbolRow(
                s.Id, s.Kind, s.Name, s.Signature, s.ReturnType, s.FieldId, s.LineNumber))
            .ToListAsync(ct);

        var variables = await _db.OeModuleVariables.AsNoTracking()
            .Where(v => v.ObjectId == objectId)
            .OrderBy(v => v.Name)
            .Select(v => new ObjectVariableRow(
                v.Id, v.Name, v.TypeKeyword, v.TypeName,
                v.TargetAppId, v.TargetObjectKind, v.TargetObjectId, v.TargetObjectName))
            .ToListAsync(ct);

        return new ObjectDetail(
            Id: header.Id,
            Kind: header.Kind,
            ObjectId: header.ObjectId,
            Name: header.Name,
            Namespace: header.Namespace,
            ModuleId: header.ModuleId,
            ModuleName: header.ModuleName,
            ExtendsAppId: header.ExtendsAppId,
            ExtendsObjectName: header.ExtendsObjectName,
            SourceFileId: header.SourceFileId,
            SourceFilePath: header.SourceFilePath,
            LineNumber: header.LineNumber,
            Symbols: symbols,
            Variables: variables);
    }

    // ── Forward-edge MCP surface (#180) ───────────────────────────────

    /// <summary>
    /// Resolves an object by case-insensitive (kind, name) within a
    /// release, used by the forward-edge MCP tools to translate the
    /// agent's natural "Sales-Post" form into the row id everything
    /// downstream keys on. Returns null when no match — tool wrappers
    /// throw <c>McpException</c> with a "try search_objects" hint.
    /// </summary>
    public async Task<ObjectDetail?> GetObjectByNameAsync(
        int releaseId,
        string objectKind,
        string objectName,
        CancellationToken ct = default)
    {
        var kind = objectKind.Trim().ToLowerInvariant();
        var name = objectName.Trim().ToLowerInvariant();
        var id = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == releaseId
                        && o.Kind == kind
                        && o.Name.ToLower() == name)
            .Select(o => (long?)o.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (id is null) return null;
        return await GetObjectAsync(id.Value, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Projects an object's header + symbol rows into the slim outline
    /// shape the <c>get_object_outline</c> MCP tool returns. Skips the
    /// variables list (not used for trace navigation) and the
    /// inspector-only namespace / extends-* columns. Reads the rows
    /// directly rather than going through <see cref="GetObjectAsync"/>
    /// so the variables query doesn't run and the symbols come back
    /// already sorted by line number (agent-friendly top-to-bottom
    /// reading; the inspector's kind+name sort is the wrong grain
    /// here).
    /// </summary>
    public async Task<ObjectOutline?> GetObjectOutlineAsync(
        int releaseId,
        string objectKind,
        string objectName,
        CancellationToken ct = default)
    {
        var kind = objectKind.Trim().ToLowerInvariant();
        var name = objectName.Trim().ToLowerInvariant();
        var header = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == releaseId
                        && o.Kind == kind
                        && o.Name.ToLower() == name)
            .Select(o => new
            {
                o.Id, o.Kind, o.ObjectId, o.Name, o.ModuleId,
                ModuleName = o.Module!.Name,
                o.SourceFileId,
                SourceFilePath = o.SourceFile != null ? o.SourceFile.Path : null,
                o.LineNumber,
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (header is null) return null;

        var symbols = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.ObjectId == header.Id)
            .OrderBy(s => s.LineNumber).ThenBy(s => s.Name)
            .Select(s => new ObjectSymbolRow(
                s.Id, s.Kind, s.Name, s.Signature, s.ReturnType, s.FieldId, s.LineNumber))
            .ToListAsync(ct).ConfigureAwait(false);

        // For interface targets, attach the implementing codeunits so
        // MCP agents calling get_object_outline on an interface get the
        // same "implemented by" surface the source-viewer outline shows.
        IReadOnlyList<InterfaceImplementer>? implementedBy = null;
        if (string.Equals(header.Kind, "interface", StringComparison.OrdinalIgnoreCase))
        {
            var rows = await _references.FindInterfaceImplementersAsync(header.ModuleId, header.Name, ct);
            implementedBy = rows
                .Select(r => new InterfaceImplementer(r.SourceObjectId, r.SourceObjectName, r.SourceModuleName))
                .ToList();
        }

        return new ObjectOutline(
            Id: header.Id,
            Kind: header.Kind,
            ObjectId: header.ObjectId,
            Name: header.Name,
            ModuleId: header.ModuleId,
            ModuleName: header.ModuleName,
            SourceFileId: header.SourceFileId,
            SourceFilePath: header.SourceFilePath,
            LineNumber: header.LineNumber,
            Symbols: symbols,
            ImplementedBy: implementedBy);
    }

    /// <summary>
    /// Returns the AL source slice for a procedure / trigger / event
    /// publisher / event subscriber, looked up by <see cref="ModuleSymbol.Id"/>.
    /// Slices from the declaration line through the body's matching
    /// <c>end;</c>; when <c>EndLine</c> is null (legacy / pre-#181
    /// ingest), falls back to the next-sibling-symbol's start line as
    /// an approximation. Applies <paramref name="maxLines"/> as a cap
    /// and stamps <see cref="ProcedureSource.Truncated"/> when applied.
    /// Returns null when the symbol id doesn't exist or doesn't have a
    /// source file attached to its parent object.
    /// </summary>
    public async Task<ProcedureSource?> GetProcedureSourceAsync(
        long symbolId,
        int maxLines,
        CancellationToken ct = default)
    {
        var row = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Id == symbolId)
            .Select(s => new
            {
                s.Id,
                s.Kind,
                s.Name,
                s.Signature,
                s.ReturnType,
                s.LineNumber,
                s.EndLine,
                OwnerId = s.Object!.Id,
                OwnerName = s.Object.Name,
                OwnerKind = s.Object.Kind,
                SourceFileId = s.Object.SourceFileId,
            })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null || row.SourceFileId is null) return null;

        // Fallback for legacy rows: take the next symbol's line on the
        // same owner as the close, minus one. The (Owner, LineNumber)
        // lookup is satisfied by ix_oe_module_symbols_object_line.
        int endLine;
        if (row.EndLine is int explicitEnd)
        {
            endLine = explicitEnd;
        }
        else
        {
            var nextLine = await _db.OeModuleSymbols.AsNoTracking()
                .Where(s => s.ObjectId == row.OwnerId && s.LineNumber > row.LineNumber)
                .OrderBy(s => s.LineNumber)
                .Select(s => (int?)s.LineNumber)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            endLine = nextLine is int n ? n - 1 : int.MaxValue;
        }

        var fileContent = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == row.SourceFileId)
            .Select(f => f.FileContent!.Content)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (fileContent is null) return null;

        var lines = fileContent.Split('\n');
        var startIdx = Math.Max(0, row.LineNumber - 1);
        var endIdx = Math.Min(lines.Length - 1, endLine - 1);
        if (endIdx < startIdx) endIdx = startIdx;
        var available = endIdx - startIdx + 1;
        var truncated = false;
        if (available > maxLines)
        {
            endIdx = startIdx + maxLines - 1;
            truncated = true;
        }
        var sliceLines = new string[endIdx - startIdx + 1];
        Array.Copy(lines, startIdx, sliceLines, 0, sliceLines.Length);
        var source = string.Join('\n', sliceLines);
        if (truncated)
        {
            source += $"\n// … (truncated at {maxLines} of {available} lines; call list_procedure_calls or narrow the question)";
        }

        return new ProcedureSource(
            SymbolId: row.Id,
            ObjectName: row.OwnerName,
            ObjectKind: row.OwnerKind,
            Kind: row.Kind,
            Name: row.Name,
            Signature: row.Signature,
            ReturnType: row.ReturnType,
            StartLine: row.LineNumber,
            EndLine: endLine == int.MaxValue ? lines.Length : endLine,
            Truncated: truncated,
            Source: source);
    }

    /// <summary>
    /// Returns the outgoing references emitted from inside the body of
    /// the procedure / trigger identified by <paramref name="symbolId"/>.
    /// The primary path keys on <c>source_symbol_id</c> (stamped at
    /// import time on rows from #181-or-later ingests); when the symbol
    /// has <c>EndLine</c> set but no references carry the FK, falls
    /// back to <c>(SourceObjectId, LineNumber BETWEEN start AND end)</c>
    /// so the tool stays useful on releases imported before the
    /// migration. Capped at <paramref name="maxResults"/>.
    /// </summary>
    public async Task<IReadOnlyList<ProcedureCall>?> ListProcedureCallsAsync(
        long symbolId,
        int maxResults,
        CancellationToken ct = default)
    {
        var symbol = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Id == symbolId)
            .Select(s => new
            {
                OwnerId = s.Object!.Id,
                s.LineNumber,
                s.EndLine,
            })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (symbol is null) return null;

        // Primary indexed seek via ix_oe_module_references_source_symbol.
        var direct = await _db.OeModuleReferences.AsNoTracking()
            .Where(r => r.SourceSymbolId == symbolId)
            .OrderBy(r => r.LineNumber).ThenBy(r => r.ColumnNumber).ThenBy(r => r.Id)
            .Take(maxResults)
            .Select(r => new ProcedureCall(
                r.Id, r.TargetAppId, r.TargetObjectKind, r.TargetObjectId, r.TargetObjectName,
                r.TargetMemberName, r.TargetMemberKind, r.ReferenceKind, r.LineNumber, r.ColumnNumber))
            .ToListAsync(ct).ConfigureAwait(false);
        if (direct.Count > 0 || symbol.EndLine is null) return direct;

        // Lazy-backfill fallback: rows from pre-#181 ingests don't carry
        // source_symbol_id. Scope by line range — only safe when EndLine
        // is set so we know where the body closes. Slower path; the
        // expectation is to migrate releases off this fallback over time
        // by re-ingesting.
        var endLine = symbol.EndLine.Value;
        var startLine = symbol.LineNumber;
        return await _db.OeModuleReferences.AsNoTracking()
            .Where(r => r.SourceObjectId == symbol.OwnerId
                        && r.SourceSymbolId == null
                        && r.LineNumber >= startLine
                        && r.LineNumber <= endLine)
            .OrderBy(r => r.LineNumber).ThenBy(r => r.ColumnNumber).ThenBy(r => r.Id)
            .Take(maxResults)
            .Select(r => new ProcedureCall(
                r.Id, r.TargetAppId, r.TargetObjectKind, r.TargetObjectId, r.TargetObjectName,
                r.TargetMemberName, r.TargetMemberKind, r.ReferenceKind, r.LineNumber, r.ColumnNumber))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lightweight module list used by the search-filter dropdown on the
    /// Release search page. Test / internal / language-pack flags don't
    /// matter for the filter widget so they're omitted; ordering matches
    /// the main module list page so the dropdown feels familiar.
    /// </summary>
    public Task<List<ReleaseModuleSummary>> ListModuleSummariesAsync(int releaseId, CancellationToken ct = default)
        => _db.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == releaseId)
            .OrderBy(m => m.Publisher).ThenBy(m => m.Name)
            .Select(m => new ReleaseModuleSummary(m.Id, m.Name, m.Publisher))
            .ToListAsync(ct);

}
