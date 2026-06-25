using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Cross-module search within a BC release: object / procedure / content
/// search plus the kind and namespace filter-option lists. Ranking and the
/// "Tell Me"-style token parsing live in <see cref="ObjectSearchRanking"/>.
/// Split out of <see cref="ObjectExplorerService"/> so the search surface
/// stands on its own. All reads are <c>AsNoTracking</c> and respect the
/// tenant query filter on <see cref="AppDbContext"/>.
/// </summary>
public sealed class ObjectSearchService
{
    private readonly AppDbContext _db;

    public ObjectSearchService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Resolves the "winning" module ids for a Release's visible chain — the
    /// same recursive-CTE + app-id shadowing the find-references queries use
    /// (<see cref="ReleaseAncestrySql.WinningModules"/>): walk
    /// <c>parent_release_id</c> upward and keep, per app id, the module closest
    /// to the seed. Used to widen the single-Release search surfaces to include
    /// objects inherited from a parent (base) Release.
    ///
    /// <para><b>Tenant fence.</b> The CTE runs as raw SQL and bypasses the EF
    /// query filter, but the ids it returns are only ever fed back into an
    /// org-filtered <c>OeModuleObjects</c> / <c>OeModuleSymbols</c> query, so a
    /// module from another tenant's chain can't surface a foreign object row.
    /// A parent chain never crosses an org boundary (parents are picked from
    /// the same org at import time). No <c>IgnoreQueryFilters</c>.</para>
    /// </summary>
    private async Task<IReadOnlyList<long>> ResolveWinningModuleIdsAsync(int releaseId, CancellationToken ct)
    {
        const string sql = ReleaseAncestrySql.WinningModules + "\n" + """
            SELECT w.id AS "Value" FROM winning w
            """;
        return await _db.Database.SqlQueryRaw<long>(sql, releaseId).ToListAsync(ct);
    }

    /// <summary>
    /// Searches every Module in a Release for objects matching the supplied
    /// kind + name/id filter, ordered by module then kind then name. Bounded
    /// by <paramref name="take"/> so a wide-open search ("just kind=table") on
    /// a 100-app DVD doesn't dump 5000 rows into the browser at once. The UI
    /// nudges the user to narrow the query when the cap is hit.
    /// <para>When <paramref name="includeInherited"/> is set, the search widens
    /// to the Release's whole visible chain (base objects inherited from a
    /// parent Release), not just the Release's own modules.</para>
    /// </summary>
    public async Task<List<ReleaseObjectMatch>> SearchObjectsInReleaseAsync(
        int releaseId, ObjectListFilter filter, int take = 200,
        bool includeInherited = false, CancellationToken ct = default)
    {
        var winning = includeInherited ? await ResolveWinningModuleIdsAsync(releaseId, ct) : null;
        var q = BuildFilteredQuery(releaseId, filter, moduleId: null, namespacePrefix: null, winning, out var tokens);
        return await ObjectSearchRanking.ExecuteAndRankAsync(q, tokens, take, ct);
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
        bool includeInherited = false,
        CancellationToken ct = default)
    {
        var winning = includeInherited ? await ResolveWinningModuleIdsAsync(releaseId, ct) : null;
        var q = BuildFilteredQuery(releaseId, filter, moduleId, namespacePrefix, winning, out var tokens);
        return await ObjectSearchRanking.ExecuteAndRankAsync(q, tokens, take, ct);
    }

    /// <summary>
    /// A single page of objects for the release-detail grid, ordered by an
    /// explicit column instead of relevance, with offset paging so the UI can
    /// lazy-load the next batch as the user scrolls. Returns the window plus
    /// the total (filtered) count. Use this when the user has picked a sort
    /// column or is browsing without a search term; the relevance-ranked
    /// <see cref="SearchObjectsInReleaseAsync(int, ObjectListFilter, long?, string?, int, CancellationToken)"/>
    /// stays the path for "best match first" text search.
    /// </summary>
    public async Task<ObjectSearchPage> SearchObjectsPageInReleaseAsync(
        int releaseId,
        ObjectListFilter filter,
        long? moduleId,
        string? namespacePrefix,
        ObjectSortColumn sortColumn,
        bool descending,
        int skip,
        int take,
        bool includeInherited = false,
        CancellationToken ct = default)
    {
        var winning = includeInherited ? await ResolveWinningModuleIdsAsync(releaseId, ct) : null;
        var q = BuildFilteredQuery(releaseId, filter, moduleId, namespacePrefix, winning, out _);
        var total = await q.CountAsync(ct);
        var rows = await ApplySort(q, sortColumn, descending)
            .Skip(skip)
            .Take(take)
            .Select(o => new ReleaseObjectMatch(
                o.Id, o.Kind, o.ObjectId, o.Name, o.Namespace,
                o.ModuleId, o.Module!.Name,
                o.SourceFileId, o.LineNumber,
                o.SourceFile != null ? o.SourceFile.LineCount : 0,
                o.VersionList))
            .ToListAsync(ct);
        return new ObjectSearchPage(rows, total);
    }

    /// <summary>
    /// Shared filter assembly for the object queries: release scope, kind(s),
    /// optional module + namespace-prefix narrows, and the "Tell Me" search
    /// tokens. Returns the query plus the positive token texts the ranker
    /// scores (empty when there's no search term).
    /// </summary>
    private IQueryable<ModuleObject> BuildFilteredQuery(
        int releaseId, ObjectListFilter filter, long? moduleId, string? namespacePrefix,
        IReadOnlyList<long>? winningModuleIds,
        out IReadOnlyList<string> tokens)
    {
        // Release-scoped by default; chain-scoped (the Release's winning modules,
        // base objects included) when the caller resolved the inherited set.
        var q = winningModuleIds is null
            ? _db.OeModuleObjects.AsNoTracking().Where(o => o.Module!.ReleaseId == releaseId)
            : _db.OeModuleObjects.AsNoTracking().Where(o => winningModuleIds.Contains(o.ModuleId));

        // A leading `kind:` prefix in the search box (e.g. `t:item`) scopes the
        // query to one kind; AND it with the Object-type dropdown selection.
        // Since an object has exactly one kind, a prefix outside the selected
        // set matches nothing. The remainder matches the object name.
        var (kindFromPrefix, searchRemainder) = ObjectSearchRanking.ExtractKindPrefix(filter.Search);
        var kinds = ObjectSearchRanking.NormalizeKinds(filter.Kinds);
        if (kindFromPrefix is not null)
        {
            if (kinds is not null && !kinds.Contains(kindFromPrefix))
            {
                // Prefix kind disjoint from the dropdown selection → empty AND.
                tokens = Array.Empty<string>();
                return q.Where(o => false);
            }
            kinds = new[] { kindFromPrefix };
        }
        if (kinds is { Count: > 0 })
        {
            q = q.Where(o => kinds.Contains(o.Kind));
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
        (q, tokens) = ObjectSearchRanking.ApplySearchTokens(q, searchRemainder);
        return q;
    }

    /// <summary>
    /// Orders the object query by the chosen grid column, always appending a
    /// stable <c>Id</c> tiebreaker so offset paging can't skip or duplicate
    /// rows that tie on the sort key.
    /// </summary>
    private static IOrderedQueryable<ModuleObject> ApplySort(
        IQueryable<ModuleObject> q, ObjectSortColumn column, bool descending)
    {
        IOrderedQueryable<ModuleObject> ordered = (column, descending) switch
        {
            (ObjectSortColumn.Default, _)       => q.OrderBy(o => o.Kind).ThenBy(o => o.ObjectId).ThenBy(o => o.Module!.DependencyCount).ThenBy(o => o.Module!.Name),
            (ObjectSortColumn.Id, false)        => q.OrderBy(o => o.ObjectId),
            (ObjectSortColumn.Id, true)         => q.OrderByDescending(o => o.ObjectId),
            (ObjectSortColumn.Name, false)      => q.OrderBy(o => o.Name),
            (ObjectSortColumn.Name, true)       => q.OrderByDescending(o => o.Name),
            (ObjectSortColumn.Module, false)    => q.OrderBy(o => o.Module!.Name).ThenBy(o => o.Name),
            (ObjectSortColumn.Module, true)     => q.OrderByDescending(o => o.Module!.Name).ThenBy(o => o.Name),
            (ObjectSortColumn.Namespace, false) => q.OrderBy(o => o.Namespace).ThenBy(o => o.Name),
            (ObjectSortColumn.Namespace, true)  => q.OrderByDescending(o => o.Namespace).ThenBy(o => o.Name),
            (ObjectSortColumn.Lines, false)     => q.OrderBy(o => o.SourceFile != null ? o.SourceFile.LineCount : 0),
            (ObjectSortColumn.Lines, true)      => q.OrderByDescending(o => o.SourceFile != null ? o.SourceFile.LineCount : 0),
            (ObjectSortColumn.Type, true)       => q.OrderByDescending(o => o.Kind).ThenBy(o => o.Name),
            _                                   => q.OrderBy(o => o.Kind).ThenBy(o => o.Name),
        };
        return ordered.ThenBy(o => o.Id);
    }

    /// <summary>Distinct object kinds in a Release — feeds the "Object type" dropdown.</summary>
    public async Task<List<string>> ListObjectKindsInReleaseAsync(
        int releaseId, bool includeInherited = false, CancellationToken ct = default)
    {
        var winning = includeInherited ? await ResolveWinningModuleIdsAsync(releaseId, ct) : null;
        var q = winning is null
            ? _db.OeModuleObjects.AsNoTracking().Where(o => o.Module!.ReleaseId == releaseId)
            : _db.OeModuleObjects.AsNoTracking().Where(o => winning.Contains(o.ModuleId));
        return await q
            .Select(o => o.Kind)
            .Distinct()
            .OrderBy(k => k)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Distinct namespace prefixes in a Release for the "Namespace" filter.
    /// Returns each unique value of <c>oe_module_objects.namespace</c>,
    /// nulls dropped. The dropdown uses a typeahead so a long list (Base App
    /// has 100+ namespaces) is still navigable.
    /// </summary>
    public async Task<List<string>> ListNamespacesInReleaseAsync(
        int releaseId, bool includeInherited = false, CancellationToken ct = default)
    {
        var winning = includeInherited ? await ResolveWinningModuleIdsAsync(releaseId, ct) : null;
        var q = winning is null
            ? _db.OeModuleObjects.AsNoTracking().Where(o => o.Module!.ReleaseId == releaseId && o.Namespace != null)
            : _db.OeModuleObjects.AsNoTracking().Where(o => winning.Contains(o.ModuleId) && o.Namespace != null);
        return await q
            .Select(o => o.Namespace!)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Procedure-name search across every module in the Release. Matches the
    /// supplied substring (case-insensitive) against <c>oe_module_symbols</c>
    /// where the symbol is a procedure / internal procedure / trigger; the
    /// owning object + module names join inline so the row can render
    /// "Module / Object / Procedure" in one query. Capped at <paramref name="take"/>.
    /// </summary>
    public async Task<List<ReleaseProcedureMatch>> SearchProceduresInReleaseAsync(
        int releaseId, string? search, long? moduleId, int take = 200,
        bool includeInherited = false, CancellationToken ct = default)
    {
        var winning = includeInherited ? await ResolveWinningModuleIdsAsync(releaseId, ct) : null;
        var q = (winning is null
                ? _db.OeModuleSymbols.AsNoTracking().Where(s => s.Module!.ReleaseId == releaseId)
                : _db.OeModuleSymbols.AsNoTracking().Where(s => winning.Contains(s.ModuleId)))
            .Where(s => s.Kind == "procedure" || s.Kind == "internal_procedure" || s.Kind == "trigger");

        if (moduleId is { } mid)
        {
            q = q.Where(s => s.ModuleId == mid);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            // ILike instead of ToLower().Contains: the latter wraps the column in
            // a function (no index) and uses current-culture casing, which
            // disagrees with the OrdinalIgnoreCase confirmation on a tr-TR host.
            // Escape %/_ so a literal wildcard in the term doesn't match all. #385
            var pattern = "%" + EscapeLike(search.Trim()) + "%";
            q = q.Where(s => EF.Functions.ILike(s.Name, pattern, "\\"));
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
        var pattern = "%" + EscapeLike(needle) + "%";

        var q = _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Module!.ReleaseId == releaseId)
            // Stays org-scoped: the query is rooted at OeModuleFiles (org query
            // filter applies); the FileContent nav becomes a JOIN to the shared
            // content store, so no cross-tenant row can leak.
            // ILike (escaped) rather than ToLower().Contains — see #385.
            .Where(f => EF.Functions.ILike(f.FileContent!.Content, pattern, "\\"));

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
                Content = f.FileContent!.Content,
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
    /// Escapes the SQL <c>LIKE</c>/<c>ILIKE</c> wildcards <c>%</c> and <c>_</c>
    /// (and the escape char itself) in a user search term, so a literal wildcard
    /// matches literally rather than everything. Paired with the <c>"\\"</c>
    /// escape-character argument on <see cref="EF.Functions"/>.<c>ILike</c>. #385
    /// </summary>
    internal static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
