using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Read-only query API over the <c>oe_*</c> schema: the release / module /
/// object browse surfaces, the forward-edge MCP lookups, procedure source,
/// and the source-file viewer (content, header, outline). The outline
/// enriches interface objects with their implementers via the same
/// release-ancestry CTE (<see cref="ReleaseAncestrySql"/>) the reference
/// graph uses.
///
/// The heavier read surfaces have their own focused services:
/// <see cref="ReferenceQueryService"/> (find-references / dependencies),
/// <see cref="ObjectSearchService"/> (cross-module search),
/// <see cref="ReleaseComparisonService"/> (release diffs), and
/// <see cref="TranslationQueryService"/> (translations).
///
/// All methods are <c>AsNoTracking</c> and respect the tenant query filter
/// on <see cref="AppDbContext"/>.
/// </summary>
public class ObjectExplorerService
{
    private readonly AppDbContext _db;
    private readonly ILogger<ObjectExplorerService> _logger;

    public ObjectExplorerService(AppDbContext db, ILogger<ObjectExplorerService> logger)
    {
        _db = db;
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
                r.Id, r.Label, r.Kind, r.Status, r.BcVersion, r.ParentReleaseId, r.ImportedAt,
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

        if (!string.IsNullOrWhiteSpace(filter.Kind))
        {
            var k = filter.Kind.Trim().ToLowerInvariant();
            q = q.Where(o => o.Kind == k);
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
            var rows = await FindInterfaceImplementersAsync(header.ModuleId, header.Name, ct);
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
            .Select(f => f.Content)
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

    // ── Source file viewer ─────────────────────────────────────────────

    public Task<SourceFileDetail?> GetFileAsync(long fileId, CancellationToken ct = default)
        => _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new SourceFileDetail(f.Id, f.ModuleId, f.Path, f.Content, f.LineCount))
            .SingleOrDefaultAsync(ct)!;

    /// <summary>
    /// Header projection for the source-file viewer's breadcrumb. Separate
    /// from <see cref="GetFileAsync"/> so the breadcrumb call doesn't have
    /// to drag the full Content blob through.
    /// </summary>
    public Task<SourceFileHeader?> GetFileHeaderAsync(long fileId, CancellationToken ct = default)
        => _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new SourceFileHeader(
                f.Id, f.ModuleId, f.Module!.Name,
                f.Module.ReleaseId, f.Module.Release!.Label,
                f.Path, f.LineCount,
                // AL enforces one object per file in practice so picking the
                // first attached object's namespace is unambiguous. ModuleFile
                // has no inverse collection nav onto ModuleObject (the FK
                // direction is one-way, with SetNull on delete), so this is
                // a correlated subquery rather than a navigation traversal.
                // Skips gracefully when the file isn't backing an object.
                _db.OeModuleObjects.AsNoTracking()
                    .Where(o => o.SourceFileId == f.Id && o.Namespace != null)
                    .Select(o => o.Namespace)
                    .FirstOrDefault()))
            .SingleOrDefaultAsync(ct)!;

    /// <summary>
    /// Flattens objects + their symbols inside a single source file into one
    /// outline list ordered by line. Feeds the right-hand "outline" panel on
    /// the source viewer.
    /// </summary>
    public async Task<List<SourceFileOutlineItem>> GetFileOutlineAsync(long fileId, CancellationToken ct = default)
    {
        var objects = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.SourceFileId == fileId)
            .Select(o => new { o.Id, o.Kind, o.Name, o.LineNumber, o.ModuleId })
            .ToListAsync(ct);

        var symbols = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Object!.SourceFileId == fileId)
            .Where(s => s.LineNumber > 0)
            .Select(s => new { s.Id, s.ObjectId, s.Kind, s.Name, s.Signature, s.LineNumber, s.EndLine })
            .ToListAsync(ct);

        var items = new List<SourceFileOutlineItem>(objects.Count + symbols.Count);
        foreach (var o in objects)
        {
            items.Add(new SourceFileOutlineItem(o.Kind, o.Name, null, o.LineNumber, o.Id));
        }
        foreach (var s in symbols)
        {
            items.Add(new SourceFileOutlineItem(s.Kind, s.Name, s.Signature, s.LineNumber, null, s.Id, s.EndLine));
        }

        // For interface files, append synthetic "implemented_by" rows
        // for every codeunit in the visible module chain that declares
        // this interface in its `implements` clause. Synthetic items
        // carry LineNumber = int.MaxValue so they sort to the bottom of
        // the outline; the source-viewer's outline grouper buckets them
        // into a dedicated "IMPLEMENTED BY" section.
        var interfaceObj = objects.FirstOrDefault(o => string.Equals(o.Kind, "interface", StringComparison.OrdinalIgnoreCase));
        if (interfaceObj is not null)
        {
            var implementers = await FindInterfaceImplementersAsync(
                interfaceObj.ModuleId, interfaceObj.Name, ct);
            foreach (var impl in implementers)
            {
                items.Add(new SourceFileOutlineItem(
                    Kind: "implemented_by",
                    Name: impl.SourceObjectName,
                    Signature: impl.SourceModuleName,
                    LineNumber: int.MaxValue,
                    ObjectId: impl.SourceObjectId));
            }
        }

        return items.OrderBy(i => i.LineNumber).ToList();
    }

    /// <summary>
    /// Returns the codeunits (across the visible module chain seeded by
    /// the interface's defining module) that declare themselves as
    /// implementing the named interface. Backed by the
    /// <c>implements_interface</c> reference rows emitted at import.
    /// </summary>
    private async Task<List<InterfaceImplementerRow>> FindInterfaceImplementersAsync(
        long interfaceModuleId, string interfaceName, CancellationToken ct)
    {
        // Resolve the release the interface lives in so we can seed the
        // recursive CTE with it — same shadowing rule as the rest of the
        // family.
        var seed = await _db.OeModules.AsNoTracking()
            .Where(m => m.Id == interfaceModuleId)
            .Select(m => new { m.ReleaseId, m.AppId })
            .FirstOrDefaultAsync(ct);
        if (seed is null) return new();

        const string sql = ReleaseAncestrySql.WinningModules + "\n" + """
            SELECT
                so.id                    AS "SourceObjectId",
                so.name                  AS "SourceObjectName",
                sm.name                  AS "SourceModuleName"
            FROM oe_module_references mr
            JOIN oe_module_objects    so ON so.id = mr.source_object_id
            JOIN oe_modules           sm ON sm.id = mr.module_id
            JOIN winning              w  ON w.id  = mr.module_id
            WHERE mr.reference_kind     = 'implements_interface'
              AND mr.target_app_id      = {1}::uuid
              AND mr.target_object_kind = 'interface'
              AND mr.target_object_name = {2}::text
            ORDER BY sm.name, so.name
            """;

        return await _db.Database
            .SqlQueryRaw<InterfaceImplementerRow>(sql, seed.ReleaseId, seed.AppId, interfaceName)
            .ToListAsync(ct);
    }

    private sealed record InterfaceImplementerRow(
        long SourceObjectId,
        string SourceObjectName,
        string SourceModuleName);

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

    // ── Source-viewer navigation ──────────────────────────────────────

    /// <summary>
    /// Returns decoration ranges the source viewer can stamp onto each
    /// object-header token so it hovers, underlines, and surfaces the
    /// "Find references" right-click menu. The <c>SymbolId</c> on each row
    /// is the <c>oe_module_objects.id</c> — the page maps it back into a
    /// navigation to the object detail's Find-references panel.
    /// </summary>
    public async Task<List<ALDevToolbox.Components.Shared.CodeViewerDeclaration>> ListDeclarationsInFileAsync(
        long fileId, CancellationToken ct = default)
    {
        var content = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => f.Content)
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(content)) return new();

        var objects = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.SourceFileId == fileId)
            .Select(o => new { o.Id, o.Kind, o.Name, o.LineNumber })
            .ToListAsync(ct);

        // Sub-symbol declarations (procedures, fields, triggers, event
        // subscribers). oe_module_symbols already stamps 1-based
        // line/column spans at import via AlSymbolExtractor, so we don't
        // need a re-scan here — symbol rows with LineNumber > 0 are
        // declared in source and can be made clickable directly.
        var symbols = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Object!.SourceFileId == fileId
                && s.LineNumber > 0
                && s.ColumnEnd > s.ColumnStart)
            .Select(s => new
            {
                s.Id, s.Kind, s.Name, s.LineNumber, s.ColumnStart, s.ColumnEnd,
                OwnerKind = s.Object!.Kind,
            })
            .ToListAsync(ct);

        var lines = content.Replace("\r\n", "\n").Split('\n');
        var result = new List<ALDevToolbox.Components.Shared.CodeViewerDeclaration>(objects.Count + symbols.Count);
        foreach (var obj in objects)
        {
            if (obj.LineNumber < 1 || obj.LineNumber > lines.Length) continue;
            var lineText = lines[obj.LineNumber - 1];

            // BC declarations typically quote the name —
            // `codeunit 80 "Sales-Post"`. Bare-identifier names (test code,
            // some old code) are matched as a fallback.
            int colStart, colEnd;
            var quoted = "\"" + obj.Name + "\"";
            var idx = lineText.IndexOf(quoted, StringComparison.Ordinal);
            if (idx >= 0)
            {
                colStart = idx + 1;
                colEnd = idx + 1 + quoted.Length;
            }
            else
            {
                idx = lineText.IndexOf(obj.Name, StringComparison.Ordinal);
                if (idx < 0) continue;
                colStart = idx + 1;
                colEnd = idx + 1 + obj.Name.Length;
            }

            result.Add(new ALDevToolbox.Components.Shared.CodeViewerDeclaration(
                SymbolId: obj.Id,
                Line: obj.LineNumber,
                ColumnStart: colStart,
                ColumnEnd: colEnd,
                Kind: obj.Kind,
                Name: obj.Name));
        }

        foreach (var sym in symbols)
        {
            result.Add(new ALDevToolbox.Components.Shared.CodeViewerDeclaration(
                SymbolId: sym.Id,
                Line: sym.LineNumber,
                ColumnStart: sym.ColumnStart,
                ColumnEnd: sym.ColumnEnd,
                Kind: sym.Kind,
                Name: sym.Name,
                IsMemberSymbol: true,
                OwnerKind: sym.OwnerKind));
        }

        return result;
    }

    /// <summary>
    /// Resolves a Cmd/Ctrl-click in the source viewer to a navigation
    /// target. Two strategies in order:
    ///
    /// 1. <b>Member-access</b>: when the clicked token matches a
    ///    <c>method_call</c> / <c>field_access</c> reference row on the same
    ///    file + line, follow <c>TargetSymbolId</c> to the
    ///    <see cref="ModuleSymbol"/> declaration and return its file + line.
    ///    This is the path that resolves <c>GLAcc."Account Type"</c> and
    ///    <c>ConfirmManagement.GetResponseOrDefault</c> — the dominant cases
    ///    that the legacy object-name fallback couldn't reach.
    /// 2. <b>Object-name</b>: same-Release lookup against
    ///    <c>oe_module_objects.Name</c>. Catches bare type literals like
    ///    <c>Customer</c> / <c>"Sales-Post"</c> that the extractor doesn't
    ///    emit member-rows for.
    ///
    /// Returns <c>null</c> when neither strategy matches — the page no-ops
    /// and shows the "No definition found" notice.
    /// </summary>
    public async Task<GoToDefinitionTarget?> GoToDefinitionAsync(
        long fileId, int line, int column, CancellationToken ct = default)
    {
        var meta = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new { f.Content, ReleaseId = f.Module!.ReleaseId })
            .SingleOrDefaultAsync(ct);
        if (meta is null) return null;

        var click = Services.Al.AlGoToDefinitionLocator.Inspect(meta.Content, line, column);
        if (click is null || string.IsNullOrEmpty(click.Word)) return null;
        var word = click.Word;

        // 1. Member-access strategy. Phase-2 extraction stamps
        //    method_call / field_access / event_publisher / label_use
        //    rows with (LineNumber, TargetMemberName, TargetSymbolId).
        //    Match the clicked word case-insensitively (AL identifiers
        //    are case-insensitive). Prefer rows with a resolved
        //    TargetSymbolId — those have a direct file + line via the
        //    symbol's owner object.
        var memberHit = await _db.OeModuleReferences.AsNoTracking()
            .Where(r => (r.ReferenceKind == "method_call"
                    || r.ReferenceKind == "field_access"
                    || r.ReferenceKind == "event_publisher"
                    || r.ReferenceKind == "label_use")
                && r.SourceObject!.SourceFileId == fileId
                && r.LineNumber == line
                && r.TargetMemberName != null
                && r.TargetMemberName.ToLower() == word.ToLower())
            .Where(r => r.TargetSymbolId != null)
            .Select(r => new
            {
                SymbolLine = r.TargetSymbol!.LineNumber,
                SymbolFileId = r.TargetSymbol!.Object!.SourceFileId,
            })
            .Where(x => x.SymbolFileId != null)
            .FirstOrDefaultAsync(ct);
        if (memberHit is not null)
        {
            return new GoToDefinitionTarget(memberHit.SymbolFileId!.Value, memberHit.SymbolLine);
        }

        // 2. Local-variable-declaration strategy. The click landed on
        //    an identifier that has a `VarName: Kind "TypeName"`
        //    declaration somewhere in the file — almost always a
        //    local var like `PaymentMethod: Record "Payment Method"`.
        //    The user expects Go-to-definition to land on the
        //    DECLARATION LINE in this file, not on the underlying
        //    type's source: typing `PaymentMethod` everywhere refers
        //    to the variable, so navigating to "where this variable
        //    was declared" is the IDE-conventional behaviour. The
        //    matching click on the underlined type-name token
        //    (`"Payment Method"` itself) still resolves through the
        //    object-name lookup below.
        //
        //    Earlier shape of this step navigated to the type — that
        //    was a temporary workaround for the bug where a bare
        //    variable name was getting object-name-looked-up and
        //    landing on an unrelated tableextension. With Go-to-def
        //    now ending on the declaration line, the user sees the
        //    type-name token right there and can Cmd-click it to
        //    reach the type source if they want it.
        var declLine = Services.Al.AlGoToDefinitionLocator
            .ResolveVariableDeclarationLine(meta.Content, word);
        if (declLine is int targetLine)
        {
            return new GoToDefinitionTarget(fileId, targetLine);
        }

        // 3. Same-Release lookup by object name. Chain-walk semantics (for
        //    customer Releases sitting on top of BC) are a follow-up — the
        //    dominant case is Microsoft-DVD-ingest where everything lives in
        //    a single Release.
        var target = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == meta.ReleaseId)
            .Where(o => o.SourceFileId != null)
            .Where(o => o.Name == word)
            .OrderBy(o => o.Kind)
            .Select(o => new { o.SourceFileId, o.LineNumber })
            .FirstOrDefaultAsync(ct);
        if (target?.SourceFileId is null) return null;
        return new GoToDefinitionTarget(target.SourceFileId.Value, target.LineNumber);
    }

    /// <summary>
    /// Spans inside <paramref name="fileId"/> that the source viewer should
    /// underline as resolvable. Drives the IDE-style "what's clickable"
    /// affordance: every token underlined here will, on right-click or
    /// Cmd-click, resolve to a definition via <see cref="GoToDefinitionAsync"/>.
    ///
    /// Sources from phase-2 <c>method_call</c> / <c>field_access</c> reference
    /// rows: each row carries <c>LineNumber</c> + <c>TargetMemberName</c>; we
    /// re-scan the line to recover the 1-based column range. Same scanning
    /// strategy as <see cref="ListDeclarationsInFileAsync"/> — quoted first
    /// (<c>"Account Type"</c>), bare identifier fallback. Multiple references
    /// on the same line are handled by walking forward through the line text
    /// rather than always picking the first occurrence.
    ///
    /// Variable-declaration types (<c>variable_type</c>, <c>parameter_type</c>,
    /// <c>return_type</c>) aren't included — those reference rows don't carry
    /// a line number; symbol-package extraction doesn't yield source positions.
    /// </summary>
    public async Task<List<ALDevToolbox.Components.Shared.CodeViewerResolvable>>
        ListResolvablesInFileAsync(long fileId, CancellationToken ct = default)
    {
        var content = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => f.Content)
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(content)) return new();

        // Pull every source-extracted reference on the file (LineNumber
        // set). Two row shapes contribute spans:
        //   - Member-scoped (method_call / field_access): the underlined
        //     token is the MEMBER name. Go-to-definition resolves via
        //     the row's TargetSymbolId when present, or falls back to
        //     object-name lookup.
        //   - Object-scoped (property_object from SourceTable,
        //     LookupPageID, …): the underlined token is the TARGET
        //     OBJECT name. Go-to-definition resolves via the object-name
        //     lookup. No member-symbol id needed.
        // The line-text scan below uses the per-row Name to find the
        // 1-based column span — same logic for both shapes.
        var rows = await _db.OeModuleReferences.AsNoTracking()
            .Where(r => r.SourceObject!.SourceFileId == fileId
                && r.LineNumber != null)
            .Select(r => new
            {
                Line = r.LineNumber!.Value,
                Column = r.ColumnNumber,
                Name = r.TargetMemberName ?? r.TargetObjectName,
            })
            .Where(x => x.Name != null && x.Name != "")
            .ToListAsync(ct);
        if (rows.Count == 0) return new();

        var lines = content.Replace("\r\n", "\n").Split('\n');
        var result = new List<ALDevToolbox.Components.Shared.CodeViewerResolvable>(rows.Count);
        // Group by line so the text-search fallback below can walk forward
        // through multiple references on the same line without re-finding
        // the first occurrence each time. Rows with `Column` set bypass
        // the search entirely.
        foreach (var byLine in rows.GroupBy(r => r.Line))
        {
            if (byLine.Key < 1 || byLine.Key > lines.Length) continue;
            var lineText = lines[byLine.Key - 1];
            // Track per-name search cursors for the text-search path so
            // successive occurrences of the same identifier on one line
            // each get their own span.
            var cursors = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in byLine)
            {
                // Fast path: the extractor stamped the column at emission
                // time. Use it directly and skip the text-search — the
                // search lands on the leftmost occurrence which is wrong
                // when the same identifier appears twice on a line (e.g.
                // `field("No."; Rec."No.")` should underline the RHS
                // Rec."No.", not the LHS control name).
                if (row.Column is { } colStart && colStart >= 1
                    && colStart <= lineText.Length + 1)
                {
                    var col0 = colStart - 1;
                    var nameLen = row.Name!.Length;
                    // The stored column points at the FIRST char of the
                    // identifier. If the source has a quote there, the
                    // underline span needs to include the quotes too.
                    var matchLen = (col0 < lineText.Length && lineText[col0] == '"')
                        ? nameLen + 2
                        : nameLen;
                    result.Add(new ALDevToolbox.Components.Shared.CodeViewerResolvable(
                        Line: byLine.Key,
                        ColumnStart: colStart,
                        ColumnEnd: colStart + matchLen));
                    continue;
                }

                // Fallback for legacy rows imported before column_number
                // existed: walk the line text forward to find the name.
                var quoted = "\"" + row.Name + "\"";
                var cursor = cursors.TryGetValue(row.Name!, out var c) ? c : 0;
                int idx;
                int fallbackLen;
                idx = lineText.IndexOf(quoted, cursor, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    fallbackLen = quoted.Length;
                }
                else
                {
                    idx = Services.Al.AlGoToDefinitionLocator.IndexOfWord(lineText, row.Name!, cursor);
                    if (idx < 0) continue;
                    fallbackLen = row.Name!.Length;
                }
                cursors[row.Name!] = idx + fallbackLen;
                result.Add(new ALDevToolbox.Components.Shared.CodeViewerResolvable(
                    Line: byLine.Key,
                    ColumnStart: idx + 1,
                    ColumnEnd: idx + 1 + fallbackLen));
            }
        }

        // Second pass: `extends_target` rows. The importer doesn't stamp
        // a line/column on them (the extends target sits in the object
        // header, not somewhere in the body), so they fall outside the
        // LineNumber != null filter above. Recover the range by joining
        // each row to its source object's header line and scanning that
        // line for the extends keyword + target name. The user
        // reported the `tableextension … extends "Gen. Journal Line"`
        // base name showing no underline; this is what restores it.
        var extendsRows = await _db.OeModuleReferences.AsNoTracking()
            .Where(r => r.SourceObject!.SourceFileId == fileId
                && r.ReferenceKind == "extends_target"
                && r.SourceObject!.LineNumber > 0
                && r.TargetObjectName != null)
            .Select(r => new
            {
                Line = r.SourceObject!.LineNumber,
                Name = r.TargetObjectName!,
            })
            .ToListAsync(ct);
        foreach (var row in extendsRows)
        {
            if (row.Line < 1 || row.Line > lines.Length) continue;
            var span = Services.Al.AlGoToDefinitionLocator.FindExtendsTargetSpan(lines[row.Line - 1], row.Name);
            if (span is null) continue;
            result.Add(new ALDevToolbox.Components.Shared.CodeViewerResolvable(
                Line: row.Line,
                ColumnStart: span.Value.Start,
                ColumnEnd: span.Value.End));
        }

        return result;
    }

    /// <summary>
    /// "Find in this file" — extracts the word at the supplied click position
    /// and returns every line of the same file that contains it.
    /// </summary>
    public async Task<FileWordSearch?> FindInFileAsync(
        long fileId, int line, int column, CancellationToken ct = default)
    {
        var content = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => f.Content)
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(content)) return null;

        var click = Services.Al.AlGoToDefinitionLocator.Inspect(content, line, column);
        if (click is null || string.IsNullOrEmpty(click.Word)) return null;

        var word = click.Word;
        var occurrences = new List<FileWordOccurrence>();
        var lines = content.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(word, StringComparison.Ordinal))
            {
                var trimmed = lines[i].TrimEnd();
                if (trimmed.Length > 200) trimmed = trimmed[..200] + "…";
                occurrences.Add(new FileWordOccurrence(i + 1, trimmed));
            }
        }
        return new FileWordSearch(word, occurrences);
    }

}
