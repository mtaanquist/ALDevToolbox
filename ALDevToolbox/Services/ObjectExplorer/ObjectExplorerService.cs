using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Read-only query API over the <c>oe_*</c> schema. Backs the Object Explorer
/// UI and powers find-references across the imported Release chain.
///
/// The headline query — <see cref="FindReferencesAsync"/> — uses a recursive
/// CTE over <c>oe_releases.parent_release_id</c> to compute the visible
/// "module chain" for a Release: every module in this Release plus every
/// ancestor Release, then shadowing rules collapse same-AppId duplicates so
/// the closest-to-current module wins. The query that searches references
/// then joins against that chain so a Customer Release sitting on top of a
/// BC Release sees one consistent slice of the ecosystem with no
/// double-counting and no surprises from older versions still living in
/// the parent Release.
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

    // ── Cross-module search ────────────────────────────────────────────

    /// <summary>
    /// Searches every Module in a Release for objects matching the supplied
    /// kind + name/id filter, ordered by module then kind then name. Bounded
    /// by <paramref name="take"/> so a wide-open search ("just kind=table") on
    /// a 100-app DVD doesn't dump 5000 rows into the browser at once. The UI
    /// nudges the user to narrow the query when the cap is hit.
    /// </summary>
    public async Task<List<ReleaseObjectMatch>> SearchObjectsInReleaseAsync(
        int releaseId, ObjectListFilter filter, int take = 200, CancellationToken ct = default)
    {
        var q = _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == releaseId);

        if (!string.IsNullOrWhiteSpace(filter.Kind))
        {
            var k = filter.Kind.Trim().ToLowerInvariant();
            q = q.Where(o => o.Kind == k);
        }
        (q, var tokens) = ApplySearchTokens(q, filter.Search);

        return await ExecuteAndRankAsync(q, tokens, take, ct);
    }

    /// <summary>
    /// Same as <see cref="SearchObjectsInReleaseAsync"/> but with the
    /// extension (= owning module) + namespace filters the legacy
    /// VersionBrowser exposed. <paramref name="moduleId"/> narrows to one
    /// module; <paramref name="namespacePrefix"/> requires
    /// <c>oe_module_objects.namespace</c> to start with the supplied prefix
    /// (no trailing dot — "Microsoft.Warehouse" matches both
    /// "Microsoft.Warehouse" and "Microsoft.Warehouse.ADCS").
    /// </summary>
    public async Task<List<ReleaseObjectMatch>> SearchObjectsInReleaseAsync(
        int releaseId,
        ObjectListFilter filter,
        long? moduleId,
        string? namespacePrefix,
        int take = 500,
        CancellationToken ct = default)
    {
        var q = _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == releaseId);

        if (!string.IsNullOrWhiteSpace(filter.Kind))
        {
            var k = filter.Kind.Trim().ToLowerInvariant();
            q = q.Where(o => o.Kind == k);
        }
        if (moduleId is { } mid)
        {
            q = q.Where(o => o.ModuleId == mid);
        }
        if (!string.IsNullOrWhiteSpace(namespacePrefix))
        {
            var ns = namespacePrefix.Trim();
            q = q.Where(o => o.Namespace != null && o.Namespace.StartsWith(ns));
        }
        (q, var tokens) = ApplySearchTokens(q, filter.Search);

        return await ExecuteAndRankAsync(q, tokens, take, ct);
    }

    /// <summary>
    /// BC "Tell Me" style query parser. Splits the search string into tokens
    /// and AND's one predicate per token onto the queryable, so every token
    /// must appear (case-insensitive substring) in the object name for a row
    /// to match — "sal set" finds "Sales &amp; Receivables Setup" but
    /// "sal xyz" finds nothing. Three modifiers refine that base behaviour:
    /// <list type="bullet">
    ///   <item>a double-quoted run is one literal token with its spaces
    ///   preserved, so <c>"sales header"</c> matches that exact phrase rather
    ///   than the two words anywhere in the name;</item>
    ///   <item>a <c>-</c> prefix negates a token (<c>setup -temp</c> keeps
    ///   Setup objects but drops any whose name contains "temp");</item>
    ///   <item>a single bare numeric token preserves the legacy id-or-name
    ///   behaviour the query API has always offered.</item>
    /// </list>
    /// Returns the positive (non-negated) token texts so the caller can rank
    /// matches; negated tokens only filter and never contribute to the score.
    /// </summary>
    private static (IQueryable<ModuleObject> Query, IReadOnlyList<string> Tokens)
        ApplySearchTokens(IQueryable<ModuleObject> q, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return (q, Array.Empty<string>());

        var tokens = TokenizeSearch(search);
        if (tokens.Count == 0)
            return (q, Array.Empty<string>());

        if (tokens.Count == 1 && tokens[0] is { Negated: false, Quoted: false } single
            && int.TryParse(single.Text, out var asInt))
        {
            var lower = single.Text;
            q = q.Where(o => o.ObjectId == asInt || o.Name.ToLower().Contains(lower));
            // Numeric path: ranking by name tokens would be misleading
            // because the id branch matched without a name hit.
            return (q, Array.Empty<string>());
        }

        var rankTokens = new List<string>();
        foreach (var token in tokens)
        {
            var text = token.Text;
            if (token.Negated)
            {
                q = q.Where(o => !o.Name.ToLower().Contains(text));
            }
            else
            {
                q = q.Where(o => o.Name.ToLower().Contains(text));
                rankTokens.Add(text);
            }
        }
        return (q, rankTokens);
    }

    private readonly record struct SearchToken(string Text, bool Negated, bool Quoted);

    /// <summary>
    /// Hand-rolled tokenizer for the search box. Walks the string once,
    /// honouring an optional leading <c>-</c> (negation) and double-quoted
    /// runs (a literal phrase, spaces and all). Tokens are lower-cased for
    /// case-insensitive matching; empty tokens (a lone <c>-</c> or <c>""</c>)
    /// are dropped.
    /// </summary>
    private static List<SearchToken> TokenizeSearch(string search)
    {
        var tokens = new List<SearchToken>();
        var i = 0;
        var n = search.Length;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(search[i])) i++;
            if (i >= n) break;

            var negated = search[i] == '-';
            if (negated) i++;

            string text;
            bool quoted;
            if (i < n && search[i] == '"')
            {
                quoted = true;
                i++; // opening quote
                var start = i;
                while (i < n && search[i] != '"') i++;
                text = search[start..i];
                if (i < n) i++; // closing quote
            }
            else
            {
                quoted = false;
                var start = i;
                while (i < n && !char.IsWhiteSpace(search[i])) i++;
                text = search[start..i];
            }

            if (text.Length == 0) continue;
            tokens.Add(new SearchToken(text.ToLowerInvariant(), negated, quoted));
        }
        return tokens;
    }


    /// <summary>
    /// Materialises the filtered query under the legacy (Kind, ObjectId,
    /// Name) DB order — that order remains the result contract when no
    /// search is supplied — and, when search tokens are present, re-ranks
    /// the page in memory so word-boundary hits float above mid-word hits.
    /// The in-memory pass is bounded by <paramref name="take"/>, so the
    /// extra work is small even on releases with thousands of objects.
    /// </summary>
    private static async Task<List<ReleaseObjectMatch>> ExecuteAndRankAsync(
        IQueryable<ModuleObject> q,
        IReadOnlyList<string> tokens,
        int take,
        CancellationToken ct)
    {
        var rows = await q
            .OrderBy(o => o.Kind).ThenBy(o => o.ObjectId).ThenBy(o => o.Name)
            .Take(take)
            .Select(o => new ReleaseObjectMatch(
                o.Id, o.Kind, o.ObjectId, o.Name, o.Namespace,
                o.ModuleId, o.Module!.Name,
                o.SourceFileId, o.LineNumber,
                o.SourceFile != null ? o.SourceFile.LineCount : 0))
            .ToListAsync(ct);

        if (tokens.Count == 0)
            return rows;

        return rows
            .Select(r => (Row: r, Score: ScoreNameMatch(r.Name, tokens)))
            .OrderByDescending(x => x.Score.BoundaryHits)
            .ThenByDescending(x => x.Score.Earliness)
            .ThenBy(x => x.Row.Kind)
            .ThenBy(x => x.Row.ObjectId)
            .ThenBy(x => x.Row.Name)
            .Select(x => x.Row)
            .ToList();
    }

    /// <summary>
    /// Scores a candidate name against the parsed search tokens. Returns
    /// (a) the number of tokens whose first occurrence starts on a word
    /// boundary (start of name, after a separator, or at a lower→upper
    /// PascalCase transition), and (b) an "earliness" tally that favours
    /// tokens appearing near the front of the name. Higher is better on
    /// both axes; ties fall through to the Kind/ObjectId/Name tiebreakers.
    /// </summary>
    private static (int BoundaryHits, int Earliness) ScoreNameMatch(
        string name, IReadOnlyList<string> tokens)
    {
        var lower = name.ToLowerInvariant();
        var boundaryHits = 0;
        var earliness = 0;
        foreach (var token in tokens)
        {
            var idx = lower.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0) continue;
            earliness -= idx;
            if (IsWordBoundary(name, idx))
                boundaryHits++;
        }
        return (boundaryHits, earliness);
    }

    private static bool IsWordBoundary(string original, int index)
    {
        if (index == 0) return true;
        if (index >= original.Length) return false;
        var prev = original[index - 1];
        if (prev is ' ' or '.' or '-' or '_' or '&' or '/' or ',' or '(' or ')')
            return true;
        // PascalCase boundary: a lowercase character followed by an
        // uppercase one acts like a word boundary for identifiers such as
        // SalesHeader, where "Header" should rank like a fresh word.
        return char.IsLower(prev) && char.IsUpper(original[index]);
    }

    /// <summary>Distinct object kinds in a Release — feeds the "Object type" dropdown.</summary>
    public Task<List<string>> ListObjectKindsInReleaseAsync(int releaseId, CancellationToken ct = default)
        => _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == releaseId)
            .Select(o => o.Kind)
            .Distinct()
            .OrderBy(k => k)
            .ToListAsync(ct);

    /// <summary>
    /// Distinct namespace prefixes in a Release for the "Namespace" filter.
    /// Returns each unique value of <c>oe_module_objects.namespace</c>,
    /// nulls dropped. The dropdown uses a typeahead so a long list (Base App
    /// has 100+ namespaces) is still navigable.
    /// </summary>
    public Task<List<string>> ListNamespacesInReleaseAsync(int releaseId, CancellationToken ct = default)
        => _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == releaseId && o.Namespace != null)
            .Select(o => o.Namespace!)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(ct);

    /// <summary>
    /// Procedure-name search across every module in the Release. Matches the
    /// supplied substring (case-insensitive) against <c>oe_module_symbols</c>
    /// where the symbol is a procedure / internal procedure / trigger; the
    /// owning object + module names join inline so the row can render
    /// "Module / Object / Procedure" in one query. Capped at <paramref name="take"/>.
    /// </summary>
    public async Task<List<ReleaseProcedureMatch>> SearchProceduresInReleaseAsync(
        int releaseId, string? search, long? moduleId, int take = 200, CancellationToken ct = default)
    {
        var q = _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Module!.ReleaseId == releaseId)
            .Where(s => s.Kind == "procedure" || s.Kind == "internal_procedure" || s.Kind == "trigger");

        if (moduleId is { } mid)
        {
            q = q.Where(s => s.ModuleId == mid);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var lower = search.Trim().ToLower();
            q = q.Where(s => s.Name.ToLower().Contains(lower));
        }

        return await q.OrderBy(s => s.Module!.Name)
            .ThenBy(s => s.Object!.Name).ThenBy(s => s.Name)
            .Take(take)
            .Select(s => new ReleaseProcedureMatch(
                s.Id,
                s.ObjectId,
                s.Object!.Kind,
                s.Object.Name,
                s.Module!.Name,
                s.Kind,
                s.Name,
                s.Signature,
                s.ReturnType,
                s.Object.SourceFileId,
                s.Object.LineNumber))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Content (text) search across every <c>oe_module_files.content</c> row
    /// in the Release. Server-side substring match (<c>LIKE '%…%'</c> on
    /// lower-cased content) — fine for small/medium releases; a follow-up
    /// can swap in a Postgres GIN trigram index when the wait gets painful.
    ///
    /// For each matching file we materialise the line containing the first
    /// hit (or the first <paramref name="maxLinesPerFile"/> hits) so the
    /// result table can show a one-line preview with the right line number
    /// to deep-link to. Capped at <paramref name="take"/> file hits.
    /// </summary>
    public async Task<List<ReleaseContentMatch>> SearchContentInReleaseAsync(
        int releaseId, string search, long? moduleId, int take = 100, int maxLinesPerFile = 3, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(search);
        var needle = search.Trim();
        var lower = needle.ToLower();

        var q = _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Module!.ReleaseId == releaseId)
            .Where(f => f.Content.ToLower().Contains(lower));

        if (moduleId is { } mid)
        {
            q = q.Where(f => f.ModuleId == mid);
        }

        // Pull (Id, Path, ModuleId, ModuleName, Content) for the candidate
        // files, then walk each line client-side to pluck the matching line
        // numbers + snippets. Bounded by `take` * `maxLinesPerFile`, which
        // keeps the worst-case payload modest even on a Base App search.
        var candidates = await q
            .OrderBy(f => f.Module!.Name).ThenBy(f => f.Path)
            .Take(take)
            .Select(f => new
            {
                f.Id,
                f.Path,
                f.ModuleId,
                ModuleName = f.Module!.Name,
                f.Content,
            })
            .ToListAsync(ct);

        var results = new List<ReleaseContentMatch>(candidates.Count);
        foreach (var c in candidates)
        {
            var added = 0;
            var lines = c.Content.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length && added < maxLinesPerFile; i++)
            {
                if (lines[i].Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    var snippet = lines[i].Trim();
                    if (snippet.Length > 200) snippet = snippet[..200] + "…";
                    results.Add(new ReleaseContentMatch(
                        FileId: c.Id,
                        FilePath: c.Path,
                        ModuleId: c.ModuleId,
                        ModuleName: c.ModuleName,
                        LineNumber: i + 1,
                        Snippet: snippet));
                    added++;
                }
            }
        }
        return results;
    }

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

        const string sql = """
            WITH RECURSIVE chain AS (
                SELECT id, parent_release_id, 0 AS depth
                FROM oe_releases
                WHERE id = {0}
                UNION ALL
                SELECT r.id, r.parent_release_id, c.depth + 1
                FROM oe_releases r
                JOIN chain c ON r.id = c.parent_release_id
            ),
            winning AS (
                SELECT DISTINCT ON (m.app_id) m.id, m.app_id
                FROM oe_modules m
                JOIN chain c ON c.id = m.release_id
                ORDER BY m.app_id, c.depth ASC
            )
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

    // ── Release comparison ────────────────────────────────────────────

    /// <summary>
    /// Module-and-file-level diff between two Releases, keyed by
    /// <c>AppId</c> for modules and canonical <c>Path</c> for files inside the
    /// Changed bucket. Read-only — see <c>.design/object-explorer.md</c> for
    /// why <c>ModuleFile.Path</c> is canonicalised at ingest, which is what
    /// makes the path-based file join trustworthy across releases.
    ///
    /// Returns null when either release id doesn't exist (or is soft-deleted).
    /// </summary>
    public async Task<ReleaseCompareSummary?> CompareReleasesAsync(
        int leftReleaseId, int rightReleaseId, CancellationToken ct = default)
    {
        var releases = await _db.OeReleases.AsNoTracking()
            .Where(r => r.Id == leftReleaseId || r.Id == rightReleaseId)
            .Where(r => r.DeletedAt == null)
            .Select(r => new { r.Id, r.Label })
            .ToListAsync(ct);

        var left = releases.FirstOrDefault(r => r.Id == leftReleaseId);
        var right = releases.FirstOrDefault(r => r.Id == rightReleaseId);
        if (left is null || right is null) return null;

        var leftModules = await LoadModuleCompareRowsAsync(leftReleaseId, ct);
        var rightModules = await LoadModuleCompareRowsAsync(rightReleaseId, ct);

        var leftByApp = leftModules.ToDictionary(m => m.AppId);
        var rightByApp = rightModules.ToDictionary(m => m.AppId);

        var added = new List<ModuleCompareEntry>();
        var removed = new List<ModuleCompareEntry>();
        var changed = new List<ModuleCompareEntry>();

        foreach (var appId in rightByApp.Keys.Except(leftByApp.Keys))
        {
            var m = rightByApp[appId];
            added.Add(new ModuleCompareEntry(
                appId, m.Name, m.Publisher,
                LeftModuleId: null, LeftVersion: null,
                RightModuleId: m.ModuleId, RightVersion: m.Version,
                AddedFileCount: 0, RemovedFileCount: 0, ChangedFileCount: 0));
        }
        foreach (var appId in leftByApp.Keys.Except(rightByApp.Keys))
        {
            var m = leftByApp[appId];
            removed.Add(new ModuleCompareEntry(
                appId, m.Name, m.Publisher,
                LeftModuleId: m.ModuleId, LeftVersion: m.Version,
                RightModuleId: null, RightVersion: null,
                AddedFileCount: 0, RemovedFileCount: 0, ChangedFileCount: 0));
        }

        var intersection = leftByApp.Keys.Intersect(rightByApp.Keys).ToList();

        // For the Changed bucket compute per-module file diff counts in one
        // pass — load (ModuleId, Path, ContentHash) for both sides of every
        // intersection module, key into a dictionary by (ModuleId, Path),
        // walk per AppId.
        if (intersection.Count > 0)
        {
            var leftModIds = intersection.Select(a => leftByApp[a].ModuleId).ToList();
            var rightModIds = intersection.Select(a => rightByApp[a].ModuleId).ToList();

            var leftFiles = await _db.OeModuleFiles.AsNoTracking()
                .Where(f => leftModIds.Contains(f.ModuleId))
                .Select(f => new { f.ModuleId, f.Path, f.ContentHash })
                .ToListAsync(ct);
            var rightFiles = await _db.OeModuleFiles.AsNoTracking()
                .Where(f => rightModIds.Contains(f.ModuleId))
                .Select(f => new { f.ModuleId, f.Path, f.ContentHash })
                .ToListAsync(ct);

            var leftByModule = leftFiles.GroupBy(f => f.ModuleId)
                .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Path, x => x.ContentHash));
            var rightByModule = rightFiles.GroupBy(f => f.ModuleId)
                .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Path, x => x.ContentHash));

            foreach (var appId in intersection)
            {
                var lm = leftByApp[appId];
                var rm = rightByApp[appId];
                var lf = leftByModule.GetValueOrDefault(lm.ModuleId, new Dictionary<string, string>());
                var rf = rightByModule.GetValueOrDefault(rm.ModuleId, new Dictionary<string, string>());

                var addedCount = rf.Keys.Count(p => !lf.ContainsKey(p));
                var removedCount = lf.Keys.Count(p => !rf.ContainsKey(p));
                var changedCount = lf.Count(kv => rf.TryGetValue(kv.Key, out var rh) && rh != kv.Value);

                if (addedCount == 0 && removedCount == 0 && changedCount == 0)
                {
                    continue; // module unchanged — drop from Changed bucket
                }
                changed.Add(new ModuleCompareEntry(
                    appId, lm.Name, lm.Publisher,
                    LeftModuleId: lm.ModuleId, LeftVersion: lm.Version,
                    RightModuleId: rm.ModuleId, RightVersion: rm.Version,
                    AddedFileCount: addedCount,
                    RemovedFileCount: removedCount,
                    ChangedFileCount: changedCount));
            }
        }

        added = added.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        removed = removed.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        changed = changed.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();

        _logger.LogInformation(
            "CompareReleases Left={Left} Right={Right} Added={Added} Removed={Removed} Changed={Changed}",
            leftReleaseId, rightReleaseId, added.Count, removed.Count, changed.Count);

        return new ReleaseCompareSummary(
            left.Id, left.Label, right.Id, right.Label, added, removed, changed);
    }

    private record ModuleCompareRow(long ModuleId, Guid AppId, string Name, string Publisher, string Version);

    private Task<List<ModuleCompareRow>> LoadModuleCompareRowsAsync(int releaseId, CancellationToken ct)
        => _db.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == releaseId)
            .Select(m => new ModuleCompareRow(m.Id, m.AppId, m.Name, m.Publisher, m.Version))
            .ToListAsync(ct);

    /// <summary>
    /// File-pair diff for one Changed module. Files are joined on canonical
    /// <c>Path</c>. Returns null when either module id is missing.
    /// </summary>
    public async Task<ModuleFileCompareResult?> CompareModuleFilesAsync(
        long leftModuleId, long rightModuleId, CancellationToken ct = default)
    {
        var modules = await _db.OeModules.AsNoTracking()
            .Where(m => m.Id == leftModuleId || m.Id == rightModuleId)
            .Select(m => new { m.Id, m.Name })
            .ToListAsync(ct);

        if (modules.Count < 2) return null;

        var leftFiles = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.ModuleId == leftModuleId)
            .Select(f => new { f.Id, f.Path, f.LineCount, f.ContentHash })
            .ToListAsync(ct);
        var rightFiles = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.ModuleId == rightModuleId)
            .Select(f => new { f.Id, f.Path, f.LineCount, f.ContentHash })
            .ToListAsync(ct);

        var leftByPath = leftFiles.ToDictionary(f => f.Path);
        var rightByPath = rightFiles.ToDictionary(f => f.Path);

        var added = new List<FileCompareEntry>();
        var removed = new List<FileCompareEntry>();
        var changed = new List<FileCompareEntry>();

        foreach (var path in rightByPath.Keys.Except(leftByPath.Keys).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var r = rightByPath[path];
            added.Add(new FileCompareEntry(path, null, r.Id, 0, r.LineCount));
        }
        foreach (var path in leftByPath.Keys.Except(rightByPath.Keys).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var l = leftByPath[path];
            removed.Add(new FileCompareEntry(path, l.Id, null, l.LineCount, 0));
        }
        foreach (var kv in leftByPath.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!rightByPath.TryGetValue(kv.Key, out var r)) continue;
            if (string.Equals(kv.Value.ContentHash, r.ContentHash, StringComparison.Ordinal)) continue;
            changed.Add(new FileCompareEntry(kv.Key, kv.Value.Id, r.Id, kv.Value.LineCount, r.LineCount));
        }

        var moduleName = modules.FirstOrDefault(m => m.Id == leftModuleId)?.Name
                         ?? modules.First().Name;

        return new ModuleFileCompareResult(
            leftModuleId, rightModuleId, moduleName, added, removed, changed);
    }

    /// <summary>
    /// Flat per-file rows for every Added / Removed / Modified pair across all
    /// modules in the two releases — the shape the Release-page Compare scope
    /// renders directly into its result table. Empty list when either release
    /// is missing.
    /// </summary>
    public async Task<List<ReleaseCompareFileRow>> CompareReleaseFilesFlatAsync(
        int leftReleaseId, int rightReleaseId, CancellationToken ct = default)
    {
        var summary = await CompareReleasesAsync(leftReleaseId, rightReleaseId, ct);
        if (summary is null) return new();

        var rows = new List<ReleaseCompareFileRow>();

        // Added / Removed modules: every file in that module is added/removed.
        var addedRightModuleIds = summary.Added.Where(m => m.RightModuleId.HasValue)
            .Select(m => m.RightModuleId!.Value).ToList();
        var removedLeftModuleIds = summary.Removed.Where(m => m.LeftModuleId.HasValue)
            .Select(m => m.LeftModuleId!.Value).ToList();

        if (addedRightModuleIds.Count > 0)
        {
            var addedFiles = await _db.OeModuleFiles.AsNoTracking()
                .Where(f => addedRightModuleIds.Contains(f.ModuleId))
                .Select(f => new { f.Id, f.Path, f.ModuleId, ModuleAppId = f.Module!.AppId, ModuleName = f.Module!.Name })
                .ToListAsync(ct);
            rows.AddRange(addedFiles.Select(f => new ReleaseCompareFileRow(
                f.ModuleAppId, f.ModuleName, f.Path, "added",
                LeftFileId: null, RightFileId: f.Id)));
        }
        if (removedLeftModuleIds.Count > 0)
        {
            var removedFiles = await _db.OeModuleFiles.AsNoTracking()
                .Where(f => removedLeftModuleIds.Contains(f.ModuleId))
                .Select(f => new { f.Id, f.Path, f.ModuleId, ModuleAppId = f.Module!.AppId, ModuleName = f.Module!.Name })
                .ToListAsync(ct);
            rows.AddRange(removedFiles.Select(f => new ReleaseCompareFileRow(
                f.ModuleAppId, f.ModuleName, f.Path, "removed",
                LeftFileId: f.Id, RightFileId: null)));
        }

        // Changed modules: pair files by path.
        foreach (var m in summary.Changed)
        {
            if (m.LeftModuleId is not { } lm || m.RightModuleId is not { } rm) continue;
            var pairs = await CompareModuleFilesAsync(lm, rm, ct);
            if (pairs is null) continue;

            foreach (var f in pairs.Added)
            {
                rows.Add(new ReleaseCompareFileRow(m.AppId, m.Name, f.Path, "added",
                    LeftFileId: null, RightFileId: f.RightFileId));
            }
            foreach (var f in pairs.Removed)
            {
                rows.Add(new ReleaseCompareFileRow(m.AppId, m.Name, f.Path, "removed",
                    LeftFileId: f.LeftFileId, RightFileId: null));
            }
            foreach (var f in pairs.Changed)
            {
                rows.Add(new ReleaseCompareFileRow(m.AppId, m.Name, f.Path, "modified",
                    LeftFileId: f.LeftFileId, RightFileId: f.RightFileId));
            }
        }

        return rows
            .OrderBy(r => r.ModuleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Releases other than the file's own that contain a file at the same
    /// <c>(AppId, Path)</c> — populates the "Compare with release" picker on
    /// the source-file viewer. Only ready Releases that actually carry a
    /// matching file are returned, keeping the dropdown dead-link-free.
    /// </summary>
    public async Task<List<CompareTargetOption>> GetCompareTargetsAsync(
        long fileId, CancellationToken ct = default)
    {
        var anchor = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new
            {
                f.Path,
                AppId = f.Module!.AppId,
                ReleaseId = f.Module!.ReleaseId,
            })
            .SingleOrDefaultAsync(ct);
        if (anchor is null) return new();

        return await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Path == anchor.Path
                && f.Module!.AppId == anchor.AppId
                && f.Module!.ReleaseId != anchor.ReleaseId
                && f.Module!.Release!.Status == "ready"
                && f.Module!.Release!.DeletedAt == null)
            .OrderBy(f => f.Module!.Release!.Label)
            .Select(f => new CompareTargetOption(
                f.Module!.ReleaseId,
                f.Module!.Release!.Label,
                f.Id))
            .ToListAsync(ct);
    }

    // ── Outline dependencies (#148: Using / Used-by) ──────────────────

    /// <summary>
    /// Returns the file's outgoing dependencies (objects this file references)
    /// and incoming dependencies (objects elsewhere that reference this file).
    /// Self-references via <c>Rec</c> / <c>xRec</c> are filtered out on both
    /// sides; "no source" targets land with a null <c>TargetFileId</c> so the
    /// UI can render a non-clickable badge with the standard tooltip.
    ///
    /// Resolution walks the parent-release chain (ancestors only for Using,
    /// full visible chain for Used-by) via the same recursive CTE as
    /// <see cref="FindReferencesAsync"/>. Returns null when the file id
    /// doesn't exist.
    /// </summary>
    public async Task<FileDependencies?> GetFileDependenciesAsync(
        long fileId, CancellationToken ct = default)
    {
        var anchor = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new
            {
                f.ModuleId,
                AppId = f.Module!.AppId,
                ReleaseId = f.Module!.ReleaseId,
            })
            .SingleOrDefaultAsync(ct);
        if (anchor is null) return null;

        // The file's "primary objects" — usually one, occasionally a
        // pageextension shipping multiple objects per .al file.
        var ownObjects = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.SourceFileId == fileId)
            .Select(o => new { o.Id, o.Kind, o.ObjectId, o.Name })
            .ToListAsync(ct);

        if (ownObjects.Count == 0)
        {
            return new FileDependencies(Array.Empty<DependencyEntry>(), Array.Empty<DependencyEntry>());
        }

        var ownObjectIds = ownObjects.Select(o => o.Id).ToList();

        // Build the self-reference exclusion predicate components: any
        // (appId, kind, objectId|name) matching one of this file's objects.
        var selfKeys = ownObjects
            .Select(o => (Kind: o.Kind, ObjectId: o.ObjectId, Name: o.Name))
            .ToHashSet();

        bool IsSelfReference(string targetKind, int? targetId, string targetName) =>
            selfKeys.Contains((targetKind, targetId, targetName))
            || (targetId.HasValue && selfKeys.Any(k => k.Kind == targetKind && k.ObjectId == targetId))
            || selfKeys.Any(k => k.Kind == targetKind && k.Name == targetName);

        var usingList = await BuildUsingAsync(anchor.ReleaseId, anchor.AppId, ownObjectIds, IsSelfReference, ct);
        var usedByList = await BuildUsedByAsync(anchor.ReleaseId, ownObjects.Select(o => new ObjectSelfKey(o.Kind, o.ObjectId, o.Name)).ToList(), anchor.AppId, IsSelfReference, ct);

        _logger.LogInformation(
            "GetFileDependencies FileId={FileId} UsingCount={UsingCount} UsedByCount={UsedByCount}",
            fileId, usingList.Count, usedByList.Count);

        return new FileDependencies(usingList, usedByList);
    }

    private record ObjectSelfKey(string Kind, int? ObjectId, string Name);

    /// <summary>
    /// Outgoing dependencies. UNION of typed globals and explicit references,
    /// resolved through the parent-release ancestor chain to land in the
    /// "winning" target module.
    /// </summary>
    private async Task<List<DependencyEntry>> BuildUsingAsync(
        int releaseId,
        Guid ownAppId,
        IReadOnlyList<long> ownObjectIds,
        Func<string, int?, string, bool> isSelfReference,
        CancellationToken ct)
    {
        if (ownObjectIds.Count == 0) return new();

        const string sql = """
            WITH RECURSIVE chain AS (
                SELECT id, parent_release_id, 0 AS depth
                FROM oe_releases
                WHERE id = {0}
                UNION ALL
                SELECT r.id, r.parent_release_id, c.depth + 1
                FROM oe_releases r
                JOIN chain c ON r.id = c.parent_release_id
            ),
            winning AS (
                SELECT DISTINCT ON (m.app_id) m.id, m.app_id, m.name
                FROM oe_modules m
                JOIN chain c ON c.id = m.release_id
                ORDER BY m.app_id, c.depth ASC
            ),
            outgoing AS (
                SELECT mv.target_app_id, mv.target_object_kind,
                       mv.target_object_id, mv.target_object_name,
                       'variable_type'::text AS reference_kind
                FROM oe_module_variables mv
                WHERE mv.object_id = ANY({1})
                  AND mv.target_app_id IS NOT NULL
                  AND mv.target_object_kind IS NOT NULL
                  AND mv.target_object_name IS NOT NULL
                UNION ALL
                SELECT mr.target_app_id, mr.target_object_kind,
                       mr.target_object_id, mr.target_object_name,
                       mr.reference_kind
                FROM oe_module_references mr
                WHERE mr.source_object_id = ANY({1})
            )
            SELECT DISTINCT ON (o.target_app_id, o.target_object_kind, o.target_object_name)
                o.target_app_id        AS "TargetAppId",
                COALESCE(w.name, '')   AS "TargetModuleName",
                o.target_object_kind   AS "TargetObjectKind",
                o.target_object_id     AS "TargetObjectId",
                o.target_object_name   AS "TargetObjectName",
                tgt.source_file_id     AS "TargetFileId",
                tgt.line_number        AS "TargetLineNumber",
                o.reference_kind       AS "ReferenceKind"
            FROM outgoing o
            LEFT JOIN winning w ON w.app_id = o.target_app_id
            LEFT JOIN oe_module_objects tgt
                ON tgt.module_id = w.id
                AND tgt.kind = o.target_object_kind
                AND (
                    (o.target_object_id IS NOT NULL AND tgt.object_id = o.target_object_id)
                    OR (o.target_object_id IS NULL AND tgt.name = o.target_object_name)
                )
            -- Dedupe key is (app, kind, name): AL guarantees a single
            -- object per (app, kind, name), so two source rows for the
            -- same target are always the same object. The ORDER BY
            -- breaks ties so the surviving row carries the most useful
            -- payload: prefer a non-null target_object_id (lets the
            -- LEFT JOIN against oe_module_objects hit the object_id
            -- branch instead of the name branch), then prefer
            -- reference_kind alphabetically for determinism. Without
            -- this an extends_target row (object_id=null) and a
            -- variable_type row (object_id=set) for the same base
            -- table both surfaced as separate Using entries — the
            -- user-reported "Gen. Journal Line shows twice in the
            -- Using list" bug.
            ORDER BY o.target_app_id, o.target_object_kind, o.target_object_name,
                     (o.target_object_id IS NULL), o.target_object_id,
                     o.reference_kind
            """;

        var rows = await _db.Database
            .SqlQueryRaw<DependencyEntry>(sql, releaseId, ownObjectIds.ToArray())
            .ToListAsync(ct);

        return rows
            .Where(r => !isSelfReference(r.TargetObjectKind, r.TargetObjectId, r.TargetObjectName))
            .OrderBy(r => r.TargetModuleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.TargetObjectKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.TargetObjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Incoming dependencies. For each of the file's primary objects, find
    /// every <c>oe_module_references</c> row across the visible release chain
    /// whose target matches. Mirrors <see cref="FindReferencesAsync"/>'s
    /// recursive-CTE + shadowing logic but accepts an <c>IN</c> list of target
    /// objects so we can dispatch all the file's objects in one SQL round-trip.
    /// </summary>
    private async Task<List<DependencyEntry>> BuildUsedByAsync(
        int releaseId,
        IReadOnlyList<ObjectSelfKey> ownTargets,
        Guid ownAppId,
        Func<string, int?, string, bool> isSelfReference,
        CancellationToken ct)
    {
        if (ownTargets.Count == 0) return new();

        // The kind list narrows the cross-module reference fan-out cheaply.
        var kinds = ownTargets.Select(t => t.Kind).Distinct().ToArray();
        var ids = ownTargets.Where(t => t.ObjectId.HasValue).Select(t => t.ObjectId!.Value).Distinct().ToArray();
        var names = ownTargets.Select(t => t.Name).Distinct().ToArray();

        // Project the *caller* side as the dependency entry — Used-by surfaces
        // who points at this file, not the file's own objects. Walks the
        // parent-release ancestor chain only — same shape as
        // FindReferencesAsync. A child release sees its parent's callers; a
        // parent release doesn't see hits from un-attached children (matches
        // the existing find-references contract).
        const string callerSql = """
            WITH RECURSIVE chain AS (
                SELECT id, parent_release_id, 0 AS depth
                FROM oe_releases
                WHERE id = {0}
                UNION ALL
                SELECT r.id, r.parent_release_id, c.depth + 1
                FROM oe_releases r
                JOIN chain c ON r.id = c.parent_release_id
            ),
            winning AS (
                SELECT DISTINCT ON (m.app_id) m.id, m.app_id, m.name
                FROM oe_modules m
                JOIN chain c ON c.id = m.release_id
                ORDER BY m.app_id, c.depth ASC
            )
            SELECT DISTINCT ON (m.app_id, so.kind, COALESCE(so.object_id, -1), so.name)
                m.app_id              AS "TargetAppId",
                m.name                AS "TargetModuleName",
                so.kind               AS "TargetObjectKind",
                so.object_id          AS "TargetObjectId",
                so.name               AS "TargetObjectName",
                so.source_file_id     AS "TargetFileId",
                so.line_number        AS "TargetLineNumber",
                mr.reference_kind     AS "ReferenceKind"
            FROM oe_module_references mr
            JOIN oe_module_objects so ON so.id = mr.source_object_id
            JOIN oe_modules        m  ON m.id  = mr.module_id
            JOIN winning           w  ON w.id  = mr.module_id
            WHERE mr.target_app_id = {1}::uuid
              AND mr.target_object_kind = ANY({2})
              AND (
                  (mr.target_object_id IS NOT NULL AND mr.target_object_id = ANY({3}))
               OR (mr.target_object_id IS NULL AND mr.target_object_name = ANY({4}))
              )
            ORDER BY m.app_id, so.kind, COALESCE(so.object_id, -1), so.name, mr.line_number
            """;

        var callers = await _db.Database
            .SqlQueryRaw<DependencyEntry>(
                callerSql,
                releaseId,
                ownAppId,
                kinds,
                ids.Length == 0 ? new[] { -1 } : ids,
                names)
            .ToListAsync(ct);

        return callers
            .Where(r => !(r.TargetAppId == ownAppId
                && isSelfReference(r.TargetObjectKind, r.TargetObjectId, r.TargetObjectName)))
            .OrderBy(r => r.TargetModuleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.TargetObjectKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.TargetObjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

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
                    idx = IndexOfWord(lineText, row.Name!, cursor);
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
            var span = FindExtendsTargetSpan(lines[row.Line - 1], row.Name);
            if (span is null) continue;
            result.Add(new ALDevToolbox.Components.Shared.CodeViewerResolvable(
                Line: row.Line,
                ColumnStart: span.Value.Start,
                ColumnEnd: span.Value.End));
        }

        return result;
    }

    /// <summary>
    /// Finds the column span of <paramref name="targetName"/> within an
    /// object-header line, but only when it appears after the
    /// <c>extends</c> keyword. The name may be quoted (the common
    /// case for AL names with spaces, dots, or other special
    /// characters) or bare; both forms are returned with their
    /// 1-based ColumnStart and exclusive-end column. Returns null
    /// when the line doesn't actually contain an <c>extends</c>
    /// followed by this target — defensive in case the header was
    /// reformatted between import and source storage.
    /// </summary>
    private static (int Start, int End)? FindExtendsTargetSpan(string lineText, string targetName)
    {
        const string keyword = "extends";
        var kwIdx = lineText.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (kwIdx < 0) return null;
        var after = kwIdx + keyword.Length;
        // `extends` must be followed by whitespace, then the name. If
        // the keyword appears as part of another identifier (very
        // unlikely on an object-header line, but cheap to guard
        // against) bail out.
        if (after < lineText.Length && IsIdentChar(lineText[after])) return null;
        while (after < lineText.Length && char.IsWhiteSpace(lineText[after])) after++;
        if (after >= lineText.Length) return null;

        var quotedTarget = "\"" + targetName + "\"";
        var quotedIdx = lineText.IndexOf(quotedTarget, after, StringComparison.Ordinal);
        if (quotedIdx >= 0)
        {
            return (quotedIdx + 1, quotedIdx + 1 + quotedTarget.Length);
        }
        var bareIdx = IndexOfWord(lineText, targetName, after);
        if (bareIdx >= 0)
        {
            return (bareIdx + 1, bareIdx + 1 + targetName.Length);
        }
        return null;
    }

    /// <summary>
    /// Word-boundary aware IndexOf — finds <paramref name="word"/> in
    /// <paramref name="haystack"/> starting at <paramref name="start"/> only
    /// when the surrounding characters aren't AL identifier characters
    /// (letter, digit, underscore). Stops <c>Insert</c> from matching inside
    /// <c>InsertRecord</c>.
    /// </summary>
    private static int IndexOfWord(string haystack, string word, int start)
    {
        var i = start;
        while (i <= haystack.Length - word.Length)
        {
            var idx = haystack.IndexOf(word, i, StringComparison.Ordinal);
            if (idx < 0) return -1;
            var before = idx == 0 || !IsIdentChar(haystack[idx - 1]);
            var after = idx + word.Length == haystack.Length
                || !IsIdentChar(haystack[idx + word.Length]);
            if (before && after) return idx;
            i = idx + 1;
        }
        return -1;
    }

    private static bool IsIdentChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_';

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

    // ── Find references ────────────────────────────────────────────────

    /// <summary>
    /// Finds every reference in the visible module-chain of <paramref name="releaseId"/>
    /// that targets the supplied object. "Visible module chain" =
    /// <list type="number">
    ///   <item>walk <c>parent_release_id</c> up from this Release to the root;</item>
    ///   <item>among same-<c>app_id</c> Modules at any depth, the one in the closest
    ///         Release to the current one wins (shadowing);</item>
    ///   <item>references on the winning Modules are the result set.</item>
    /// </list>
    /// Reference matching uses the target triplet: <c>app_id</c> + <c>kind</c> +
    /// (id if present else name). The result is ordered by source module name
    /// then source object name so the UI can render it without further sort.
    /// </summary>
    public async Task<List<ReferenceMatch>> FindReferencesAsync(
        int releaseId, FindReferencesQuery query, CancellationToken ct = default)
    {
        // Use a parameterised raw SQL because LINQ to SQL can't express the
        // recursive CTE neatly. The SQL is bounded, documented, and lives
        // here in one place — see the class doc-comment for the resolution
        // algorithm it implements.
        const string sql = """
            WITH RECURSIVE chain AS (
                SELECT id, parent_release_id, 0 AS depth
                FROM oe_releases
                WHERE id = {0}
                UNION ALL
                SELECT r.id, r.parent_release_id, c.depth + 1
                FROM oe_releases r
                JOIN chain c ON r.id = c.parent_release_id
            ),
            -- Same AppId at multiple depths: keep the one at the smallest
            -- depth, i.e. closest to the current release.
            winning AS (
                SELECT DISTINCT ON (m.app_id) m.id, m.app_id
                FROM oe_modules m
                JOIN chain c ON c.id = m.release_id
                ORDER BY m.app_id, c.depth ASC
            )
            SELECT
                mr.id                    AS "Id",
                mr.module_id             AS "SourceModuleId",
                m.name                   AS "SourceModuleName",
                mr.source_object_id      AS "SourceObjectId",
                so.kind                  AS "SourceObjectKind",
                so.name                  AS "SourceObjectName",
                mr.reference_kind        AS "ReferenceKind",
                mr.line_number           AS "LineNumber",
                so.source_file_id        AS "SourceFileId",
                NULL::text               AS "SourceFilePath",
                NULL::text               AS "Snippet",
                CASE
                    WHEN mr.target_member_name IS NULL THEN 'object'
                    ELSE 'call'
                END                      AS "Category",
                mr.target_member_name    AS "MemberName",
                mr.target_member_kind    AS "MemberKind",
                NULL::text               AS "MemberSignature"
            FROM oe_module_references mr
            JOIN oe_module_objects so ON so.id = mr.source_object_id
            JOIN oe_modules        m  ON m.id  = mr.module_id
            JOIN winning           w  ON w.id  = mr.module_id
            WHERE mr.target_app_id      = {1}::uuid
              AND mr.target_object_kind = {2}
              AND (
                    ({3}::int IS NOT NULL AND mr.target_object_id = {3}::int)
                 OR ({3}::int IS NULL AND mr.target_object_name = {4})
              )
              -- Member filter: when the caller scoped to a procedure / field,
              -- only return rows already tagged with the matching member
              -- name + kind. Object-level rows (member_name IS NULL) drop
              -- out — they're surfaced separately via the owner-type query
              -- in FindReferencesForSymbolAsync so the UI can group them.
              AND (
                    {5}::text IS NULL
                 OR (mr.target_member_name = {5}::text
                     AND ({6}::text IS NULL OR mr.target_member_kind = {6}::text))
              )
            ORDER BY m.name, so.name, mr.id
            """;

        var matches = await _db.Database
            .SqlQueryRaw<ReferenceMatch>(
                sql,
                releaseId,
                query.TargetAppId,
                query.TargetObjectKind,
                (object?)query.TargetObjectId ?? DBNull.Value,
                query.TargetObjectName,
                (object?)query.TargetMemberName ?? DBNull.Value,
                (object?)query.TargetMemberKind ?? DBNull.Value)
            .ToListAsync(ct);

        // Enrich each match with the actual code-line snippet and its
        // file path so the References panel can render VS-Code-style rows
        // (module · L42 · the code on that line). Source-file content is
        // shared across many matches in long files, so we load each file
        // once and reuse it for every same-file row.
        matches = await EnrichReferencesWithSnippetsAsync(matches, ct);

        _logger.LogInformation(
            "FindReferences ReleaseId={ReleaseId} TargetAppId={TargetAppId} Kind={Kind} Id={Id} Name={Name} Member={Member} Matches={Count}",
            releaseId, query.TargetAppId, query.TargetObjectKind, query.TargetObjectId, query.TargetObjectName, query.TargetMemberName, matches.Count);

        return matches;
    }

    /// <summary>
    /// Member-scoped find references: returns everywhere a specific
    /// procedure / field / trigger on a specific owner object is referenced,
    /// across the visible module chain. Composed of three result sets the UI
    /// renders together (each row's <see cref="ReferenceMatch.Category"/>
    /// signals which one it came from):
    /// <list type="number">
    ///   <item><c>declaration</c> — every <c>oe_module_symbols</c> row with
    ///         the same (kind, name) on a matching owner across the chain.
    ///         Surfaces overrides, internal overloads and same-name members
    ///         on extensions.</item>
    ///   <item><c>owner_type</c> — every <c>oe_module_references</c> row
    ///         targeting the owner object at the object level
    ///         (variable_type, parameter_type, return_type, extends_target,
    ///         table_no). Indirect callers — a variable typed to this object
    ///         is a place that could call this member.</item>
    ///   <item><c>call</c> — every <c>oe_module_references</c> row already
    ///         tagged with this member (target_member_name + target_member_kind).
    ///         Empty in phase 1; populated when method-call extraction
    ///         lands in phase 2.</item>
    /// </list>
    /// Phase 1's "indirect callers" answer is honest about its limitations:
    /// declarations + owner-type references are what the existing data
    /// supports. Phase 2 will graduate the <c>call</c> bucket from empty
    /// to authoritative.
    /// </summary>
    public async Task<List<ReferenceMatch>> FindReferencesForSymbolAsync(
        int releaseId,
        FindReferencesQuery query,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(query.TargetMemberName))
        {
            throw new ArgumentException(
                "Member-scoped find requires TargetMemberName.", nameof(query));
        }

        // (1) Sibling declarations of the matched member across the chain.
        // Same recursive-CTE + winning-module shadowing the object-level
        // query uses; we then join through to oe_module_symbols by owner +
        // member name.
        const string declarationSql = """
            WITH RECURSIVE chain AS (
                SELECT id, parent_release_id, 0 AS depth
                FROM oe_releases
                WHERE id = {0}
                UNION ALL
                SELECT r.id, r.parent_release_id, c.depth + 1
                FROM oe_releases r
                JOIN chain c ON r.id = c.parent_release_id
            ),
            winning AS (
                SELECT DISTINCT ON (m.app_id) m.id, m.app_id
                FROM oe_modules m
                JOIN chain c ON c.id = m.release_id
                ORDER BY m.app_id, c.depth ASC
            )
            SELECT
                s.id                     AS "Id",
                o.module_id              AS "SourceModuleId",
                m.name                   AS "SourceModuleName",
                o.id                     AS "SourceObjectId",
                o.kind                   AS "SourceObjectKind",
                o.name                   AS "SourceObjectName",
                'declaration'::text      AS "ReferenceKind",
                s.line_number            AS "LineNumber",
                o.source_file_id         AS "SourceFileId",
                NULL::text               AS "SourceFilePath",
                NULL::text               AS "Snippet",
                'declaration'::text      AS "Category",
                s.name                   AS "MemberName",
                s.kind                   AS "MemberKind",
                s.signature              AS "MemberSignature"
            FROM oe_module_symbols s
            JOIN oe_module_objects o  ON o.id = s.object_id
            JOIN oe_modules        m  ON m.id = o.module_id
            JOIN winning           w  ON w.id = o.module_id
            WHERE m.app_id              = {1}::uuid
              AND o.kind                = {2}
              AND (
                    ({3}::int IS NOT NULL AND o.object_id = {3}::int)
                 OR ({3}::int IS NULL AND o.name = {4})
              )
              AND s.name                = {5}::text
              AND ({6}::text IS NULL OR s.kind = {6}::text)
            ORDER BY m.name, o.name, s.line_number
            """;

        var declarations = await _db.Database
            .SqlQueryRaw<ReferenceMatch>(
                declarationSql,
                releaseId,
                query.TargetAppId,
                query.TargetObjectKind,
                (object?)query.TargetObjectId ?? DBNull.Value,
                query.TargetObjectName,
                query.TargetMemberName,
                (object?)query.TargetMemberKind ?? DBNull.Value)
            .ToListAsync(ct);

        // (2) Phase-2 ready: rows already tagged with the matching member.
        // Until phase 2 lands the import-pipeline change, FindReferencesAsync
        // returns the empty set here. Reusing it keeps the call/object
        // branch logic in one place.
        var memberRefs = await FindReferencesAsync(releaseId, query, ct);

        // (3) Owner-type references at the object level. Disabled for
        // now — on large releases the bucket can run into thousands of
        // rows that aren't meaningful for most "find references" use
        // cases and dominate the panel's render time. Kept in source
        // (commented) until we decide whether to bring it back behind
        // a toggle or drop it entirely.
        //
        // var ownerObjectQuery = query with { TargetMemberName = null, TargetMemberKind = null };
        // var ownerRefs = await FindReferencesAsync(releaseId, ownerObjectQuery, ct);
        // // Restamp these as owner_type so the UI groups them under
        // // "indirect references" rather than "calls".
        // ownerRefs = ownerRefs.Select(r => r with { Category = "owner_type" }).ToList();

        // (4) Interface implementations. When the target owner is an
        // interface, also pull procedures of matching (name, kind) on
        // codeunits whose header declares them as implementing this
        // interface. Source: the `implements_interface` reference rows
        // emitted at import time. Tagged with Category = "implementation"
        // so the UI can group them under their own header.
        List<ReferenceMatch> implementations = new();
        if (string.Equals(query.TargetObjectKind, "interface", StringComparison.OrdinalIgnoreCase))
        {
            implementations = await FindInterfaceMethodImplementationsAsync(releaseId, query, ct);
        }

        // Concatenate. Declarations first (most direct), then concrete
        // member calls (phase-2 will fill this). The owner-type bucket
        // would have gone here.  EnrichReferencesWithSnippetsAsync
        // already ran inside FindReferencesAsync for the member-call
        // set; do it once for the declarations bucket so every row has
        // a snippet.
        declarations = await EnrichReferencesWithSnippetsAsync(declarations, ct);
        implementations = await EnrichReferencesWithSnippetsAsync(implementations, ct);

        var all = new List<ReferenceMatch>(declarations.Count + memberRefs.Count + implementations.Count);
        all.AddRange(declarations);
        all.AddRange(memberRefs);
        all.AddRange(implementations);

        _logger.LogInformation(
            "FindReferencesForSymbol ReleaseId={ReleaseId} Owner={Kind}/{Id}/{Name} Member={Member}/{MemberKind} Decl={DeclCount} Call={CallCount} Impl={ImplCount}",
            releaseId, query.TargetObjectKind, query.TargetObjectId, query.TargetObjectName,
            query.TargetMemberName, query.TargetMemberKind,
            declarations.Count, memberRefs.Count, implementations.Count);

        return all;
    }

    /// <summary>
    /// Returns implementing-codeunit procedure declarations for an
    /// interface method. Joins <c>oe_module_references</c> rows where
    /// <c>reference_kind = 'implements_interface'</c> targets the given
    /// interface (by name; interfaces have no numeric object id) to the
    /// referencing codeunit's <c>oe_module_symbols</c> rows of matching
    /// (name, kind). Uses the same recursive-CTE shadowing as the rest
    /// of the find-references family so results respect the visible
    /// module chain.
    /// </summary>
    private async Task<List<ReferenceMatch>> FindInterfaceMethodImplementationsAsync(
        int releaseId, FindReferencesQuery query, CancellationToken ct)
    {
        const string sql = """
            WITH RECURSIVE chain AS (
                SELECT id, parent_release_id, 0 AS depth
                FROM oe_releases
                WHERE id = {0}
                UNION ALL
                SELECT r.id, r.parent_release_id, c.depth + 1
                FROM oe_releases r
                JOIN chain c ON r.id = c.parent_release_id
            ),
            winning AS (
                SELECT DISTINCT ON (m.app_id) m.id, m.app_id
                FROM oe_modules m
                JOIN chain c ON c.id = m.release_id
                ORDER BY m.app_id, c.depth ASC
            )
            SELECT
                s.id                     AS "Id",
                so.module_id             AS "SourceModuleId",
                sm.name                  AS "SourceModuleName",
                so.id                    AS "SourceObjectId",
                so.kind                  AS "SourceObjectKind",
                so.name                  AS "SourceObjectName",
                'implementation'::text   AS "ReferenceKind",
                s.line_number            AS "LineNumber",
                so.source_file_id        AS "SourceFileId",
                NULL::text               AS "SourceFilePath",
                NULL::text               AS "Snippet",
                'implementation'::text   AS "Category",
                s.name                   AS "MemberName",
                s.kind                   AS "MemberKind",
                s.signature              AS "MemberSignature"
            FROM oe_module_references mr
            JOIN oe_module_objects    so ON so.id = mr.source_object_id
            JOIN oe_modules           sm ON sm.id = mr.module_id
            JOIN winning              w  ON w.id  = mr.module_id
            JOIN oe_module_symbols    s  ON s.object_id = so.id
            WHERE mr.reference_kind     = 'implements_interface'
              AND mr.target_app_id      = {1}::uuid
              AND mr.target_object_kind = 'interface'
              AND mr.target_object_name = {2}::text
              AND s.name                = {3}::text
              AND ({4}::text IS NULL OR s.kind = {4}::text)
            ORDER BY sm.name, so.name, s.line_number
            """;

        return await _db.Database
            .SqlQueryRaw<ReferenceMatch>(
                sql,
                releaseId,
                query.TargetAppId,
                query.TargetObjectName,
                query.TargetMemberName!,
                (object?)query.TargetMemberKind ?? DBNull.Value)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Loads source-file content once per distinct file id and stamps each
    /// <see cref="ReferenceMatch"/> with the matching line's text plus the
    /// file's path. Rows pointing at modules that ship without source
    /// (SourceFileId == null) are returned unchanged. The snippet is
    /// trimmed and capped to keep the response payload bounded.
    /// </summary>
    private async Task<List<ReferenceMatch>> EnrichReferencesWithSnippetsAsync(
        List<ReferenceMatch> matches, CancellationToken ct)
    {
        var fileIds = matches
            .Where(m => m.SourceFileId.HasValue && m.LineNumber.HasValue)
            .Select(m => m.SourceFileId!.Value)
            .Distinct()
            .ToList();
        if (fileIds.Count == 0) return matches;

        var files = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => fileIds.Contains(f.Id))
            .Select(f => new { f.Id, f.Path, f.Content })
            .ToListAsync(ct);

        var lookup = files.ToDictionary(
            f => f.Id,
            f => (Path: f.Path, Lines: SplitLines(f.Content)));

        var enriched = new List<ReferenceMatch>(matches.Count);
        foreach (var m in matches)
        {
            if (m.SourceFileId is { } fid && m.LineNumber is { } ln
                && lookup.TryGetValue(fid, out var data)
                && ln >= 1 && ln <= data.Lines.Length)
            {
                var raw = data.Lines[ln - 1].TrimEnd();
                if (raw.Length > 200) raw = raw[..200] + "…";
                enriched.Add(m with { SourceFilePath = data.Path, Snippet = raw });
            }
            else
            {
                enriched.Add(m);
            }
        }
        return enriched;
    }

    private static string[] SplitLines(string content) =>
        string.IsNullOrEmpty(content)
            ? Array.Empty<string>()
            : content.Replace("\r\n", "\n").Split('\n');

    // ── Translations ────────────────────────────────────────────────────

    /// <summary>
    /// Lists every target language that has at least one translation row in
    /// the release, with a per-language row count. Drives the MCP
    /// <c>list_translation_languages</c> tool and the admin "languages
    /// uploaded" chips on the per-release translations admin page.
    ///
    /// Filter uses a subquery against <c>oe_modules</c> rather than the
    /// <c>Module</c> navigation property: the Npgsql provider can't
    /// translate <c>GroupBy</c> when its source query carries a
    /// nav-property join, but the equivalent <c>WHERE EXISTS</c> form
    /// generates clean SQL. Same goes for <see cref="ListModuleTranslationLanguagesAsync"/>.
    /// We project to an anonymous type first and remap to the record DTO
    /// in memory so the EF translator doesn't have to materialise a
    /// record constructor inside the grouped select.
    /// </summary>
    public async Task<List<TranslationLanguageSummary>> ListTranslationLanguagesAsync(
        int releaseId, CancellationToken ct = default)
    {
        var rows = await _db.OeModuleTranslations.AsNoTracking()
            .Where(t => _db.OeModules.Any(m => m.Id == t.ModuleId && m.ReleaseId == releaseId))
            .GroupBy(t => t.LanguageCode)
            .Select(g => new { LanguageCode = g.Key, Count = g.Count() })
            .OrderBy(x => x.LanguageCode)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new TranslationLanguageSummary(r.LanguageCode, r.Count)).ToList();
    }

    /// <summary>
    /// Per-module, per-language counts — drives the admin translations
    /// page so each module row can show "da-DK · 1,247  de-DE · 1,250"
    /// chips and a per-module upload button. Same subquery + remap
    /// shape as <see cref="ListTranslationLanguagesAsync"/>.
    /// </summary>
    public async Task<List<ModuleTranslationLanguageRow>> ListModuleTranslationLanguagesAsync(
        int releaseId, CancellationToken ct = default)
    {
        var rows = await _db.OeModuleTranslations.AsNoTracking()
            .Where(t => _db.OeModules.Any(m => m.Id == t.ModuleId && m.ReleaseId == releaseId))
            .GroupBy(t => new { t.ModuleId, t.LanguageCode })
            .Select(g => new { g.Key.ModuleId, g.Key.LanguageCode, Count = g.Count() })
            .OrderBy(x => x.ModuleId).ThenBy(x => x.LanguageCode)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new ModuleTranslationLanguageRow(r.ModuleId, r.LanguageCode, r.Count)).ToList();
    }

    /// <summary>
    /// Substring search over <c>oe_module_translations.target_text</c> within
    /// a release. Backs the MCP <c>search_translations</c> tool used by an
    /// agent to map a native-language caption / error message back to the
    /// AL object that produced it. The default kind filter
    /// (<c>caption,label</c>) honours the user's stated priority — captions
    /// + errors first; tooltips are opt-in via <c>kinds=any</c>.
    /// </summary>
    public async Task<List<TranslationMatch>> SearchTranslationsInReleaseAsync(
        int releaseId,
        string query,
        string? language,
        IReadOnlySet<string>? kindFilter,
        string? moduleNamePattern,
        int maxResults,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<TranslationMatch>();
        var needle = query.Trim().ToLower();

        var q = _db.OeModuleTranslations.AsNoTracking()
            .Where(t => t.Module!.ReleaseId == releaseId)
            .Where(t => t.TargetText.ToLower().Contains(needle));

        if (!string.IsNullOrWhiteSpace(language))
        {
            // Normalise the user-supplied language to the same shape we
            // store (xx-XX). Hand-crafted MCP callers might pass "da" or
            // "da_DK" — split on '-' / '_' and uppercase the region.
            var raw = language.Trim().Replace('_', '-');
            var dash = raw.IndexOf('-');
            string normalised;
            if (dash <= 0 || dash >= raw.Length - 1)
            {
                normalised = raw.ToLowerInvariant();
                q = q.Where(t => t.LanguageCode.StartsWith(normalised));
            }
            else
            {
                normalised = raw.Substring(0, dash).ToLowerInvariant() + "-" + raw.Substring(dash + 1).ToUpperInvariant();
                q = q.Where(t => t.LanguageCode == normalised);
            }
        }

        if (kindFilter is { Count: > 0 } && !kindFilter.Contains("any"))
        {
            q = q.Where(t => kindFilter.Contains(t.Kind));
        }

        if (!string.IsNullOrWhiteSpace(moduleNamePattern))
        {
            var modPat = moduleNamePattern.Trim().ToLower();
            q = q.Where(t => t.Module!.Name.ToLower().Contains(modPat));
        }

        return await q.OrderBy(t => t.Module!.Name).ThenBy(t => t.ObjectName)
            .Take(maxResults)
            .Select(t => new TranslationMatch(
                t.Id,
                t.LanguageCode,
                t.Module!.Name,
                t.ObjectKind,
                t.ObjectName,
                t.SubKind,
                t.SubName,
                t.PropertyName,
                t.Kind,
                t.SourceText,
                t.TargetText,
                t.SymbolId))
            .ToListAsync(ct).ConfigureAwait(false);
    }
}
