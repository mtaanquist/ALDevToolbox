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
        (q, var tokens) = ObjectSearchRanking.ApplySearchTokens(q, filter.Search);

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
        (q, var tokens) = ObjectSearchRanking.ApplySearchTokens(q, filter.Search);

        return await ObjectSearchRanking.ExecuteAndRankAsync(q, tokens, take, ct);
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

}
