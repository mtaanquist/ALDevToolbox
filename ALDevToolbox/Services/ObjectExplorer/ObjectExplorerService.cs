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
                // Both aggregates run as correlated subqueries against
                // oe_module_files joined through oe_modules. The numbers feed
                // the per-release Files / Size columns on the browser.
                SourceFileCount: r.Modules.SelectMany(m => m.Files).Count(),
                SourceContentLength: r.Modules.SelectMany(m => m.Files).Sum(f => (long)f.Content.Length),
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
                mr.line_number           AS "LineNumber"
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
            ORDER BY m.name, so.name, mr.id
            """;

        var matches = await _db.Database
            .SqlQueryRaw<ReferenceMatch>(
                sql,
                releaseId,
                query.TargetAppId,
                query.TargetObjectKind,
                (object?)query.TargetObjectId ?? DBNull.Value,
                query.TargetObjectName)
            .ToListAsync(ct);

        _logger.LogInformation(
            "FindReferences ReleaseId={ReleaseId} TargetAppId={TargetAppId} Kind={Kind} Id={Id} Name={Name} Matches={Count}",
            releaseId, query.TargetAppId, query.TargetObjectKind, query.TargetObjectId, query.TargetObjectName, matches.Count);

        return matches;
    }
}
