using ALDevToolbox.Data;
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
    public Task<List<ReleaseListItem>> ListReleasesAsync(
        bool includeSoftDeleted = false, CancellationToken ct = default)
    {
        var q = _db.OeReleases.AsNoTracking().AsQueryable();
        if (!includeSoftDeleted)
        {
            q = q.Where(r => r.DeletedAt == null);
        }
        return q.OrderBy(r => r.DeletedAt == null ? 0 : 1)
            .ThenBy(r => r.Label)
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
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            var lower = s.ToLower();
            if (int.TryParse(s, out var asInt))
            {
                q = q.Where(o => o.ObjectId == asInt || o.Name.ToLower().Contains(lower));
            }
            else
            {
                q = q.Where(o => o.Name.ToLower().Contains(lower));
            }
        }

        return await q
            .OrderBy(o => o.Kind).ThenBy(o => o.ObjectId).ThenBy(o => o.Name)
            .Take(take)
            .Select(o => new ReleaseObjectMatch(
                o.Id, o.Kind, o.ObjectId, o.Name, o.Namespace,
                o.ModuleId, o.Module!.Name,
                o.SourceFileId, o.LineNumber,
                o.SourceFile != null ? o.SourceFile.LineCount : 0))
            .ToListAsync(ct);
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
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            var lower = s.ToLower();
            if (int.TryParse(s, out var asInt))
            {
                q = q.Where(o => o.ObjectId == asInt || o.Name.ToLower().Contains(lower));
            }
            else
            {
                q = q.Where(o => o.Name.ToLower().Contains(lower));
            }
        }

        return await q
            .OrderBy(o => o.Kind).ThenBy(o => o.ObjectId).ThenBy(o => o.Name)
            .Take(take)
            .Select(o => new ReleaseObjectMatch(
                o.Id, o.Kind, o.ObjectId, o.Name, o.Namespace,
                o.ModuleId, o.Module!.Name,
                o.SourceFileId, o.LineNumber,
                o.SourceFile != null ? o.SourceFile.LineCount : 0))
            .ToListAsync(ct);
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
            .Select(o => new { o.Id, o.Kind, o.Name, o.LineNumber })
            .ToListAsync(ct);

        var symbols = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Object!.SourceFileId == fileId)
            .Where(s => s.LineNumber > 0)
            .Select(s => new { s.Id, s.ObjectId, s.Kind, s.Name, s.Signature, s.LineNumber })
            .ToListAsync(ct);

        var items = new List<SourceFileOutlineItem>(objects.Count + symbols.Count);
        foreach (var o in objects)
        {
            items.Add(new SourceFileOutlineItem(o.Kind, o.Name, null, o.LineNumber, o.Id));
        }
        foreach (var s in symbols)
        {
            items.Add(new SourceFileOutlineItem(s.Kind, s.Name, s.Signature, s.LineNumber, null, s.Id));
        }
        return items.OrderBy(i => i.LineNumber).ToList();
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
        //    method_call / field_access rows with (LineNumber, TargetMemberName,
        //    TargetSymbolId). Match the clicked word case-insensitively (AL
        //    identifiers are case-insensitive). Prefer rows with a resolved
        //    TargetSymbolId — those have a direct file + line via the symbol's
        //    owner object.
        var memberHit = await _db.OeModuleReferences.AsNoTracking()
            .Where(r => (r.ReferenceKind == "method_call"
                    || r.ReferenceKind == "field_access"
                    || r.ReferenceKind == "event_publisher")
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

        // 2. Same-Release lookup by object name. Chain-walk semantics (for
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
        return result;
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

        // Concatenate. Declarations first (most direct), then concrete
        // member calls (phase-2 will fill this). The owner-type bucket
        // would have gone here.  EnrichReferencesWithSnippetsAsync
        // already ran inside FindReferencesAsync for the member-call
        // set; do it once for the declarations bucket so every row has
        // a snippet.
        declarations = await EnrichReferencesWithSnippetsAsync(declarations, ct);

        var all = new List<ReferenceMatch>(declarations.Count + memberRefs.Count);
        all.AddRange(declarations);
        all.AddRange(memberRefs);

        _logger.LogInformation(
            "FindReferencesForSymbol ReleaseId={ReleaseId} Owner={Kind}/{Id}/{Name} Member={Member}/{MemberKind} Decl={DeclCount} Call={CallCount}",
            releaseId, query.TargetObjectKind, query.TargetObjectId, query.TargetObjectName,
            query.TargetMemberName, query.TargetMemberKind,
            declarations.Count, memberRefs.Count);

        return all;
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
}
