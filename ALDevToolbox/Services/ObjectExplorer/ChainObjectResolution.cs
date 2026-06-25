using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Resolves an object (or a member symbol on one) by name across the
/// <em>visible release chain</em> seeded at a release — the project /
/// third-party-on-parent case where a referenced base object (Customer,
/// Sales Header, …) physically lives in an ancestor Release, not the seed.
///
/// <para>The seed-only object lookups in <see cref="ReferenceResolver"/>,
/// <see cref="SourceViewerService"/> and the MCP find-references tools used to
/// stop at <c>Module.ReleaseId == seed</c>, so a click on a base table from a
/// project Release resolved to nothing. This walks the same recursive
/// ancestry + app-id shadowing CTE the find-references <em>queries</em> already
/// use (<see cref="ReleaseAncestrySql.WinningModules"/>): a child Release sees
/// an ancestor's module only when it doesn't ship its own copy, and the
/// closest-depth match wins on a name collision across apps.</para>
///
/// <para><b>Tenant fence.</b> These methods run raw SQL and so bypass the EF
/// query filter — the same property the find-references queries have. Every
/// caller seeds from a release id it already obtained through an org-filtered
/// EF read (the clicked file's release, or <c>ResolveReleaseAsync</c>), and a
/// parent chain never crosses an org boundary (a parent can only be picked from
/// the same org at import time, with no <c>IgnoreQueryFilters</c>), so the walk
/// stays in-tenant. Don't call these with a release id that hasn't been
/// org-validated.</para>
/// </summary>
internal static class ChainObjectResolution
{
    /// <summary>
    /// Resolves an object by <paramref name="name"/> (optionally narrowed to
    /// <paramref name="kind"/> and/or <paramref name="objectId"/>) across the
    /// visible chain, returning the closest-depth hit or <see langword="null"/>.
    /// Base-object kinds sort ahead of their extension kinds (so a bare
    /// <c>Customer</c> token lands on the table, not a <c>tableextension</c>
    /// named the same), then the closest Release wins.
    /// </summary>
    public static async Task<ChainObjectHit?> ResolveObjectAsync(
        AppDbContext db, int seedReleaseId, string name, string? kind, int? objectId, CancellationToken ct)
    {
        const string sql = ReleaseAncestrySql.WinningModules + "\n" + """
            SELECT
                o.id             AS "Id",
                o.source_file_id AS "SourceFileId",
                o.line_number    AS "LineNumber",
                o.kind           AS "Kind",
                o.name           AS "Name",
                o.object_id      AS "ObjectId",
                m.app_id         AS "AppId",
                m.id             AS "ModuleId",
                m.name           AS "ModuleName"
            FROM oe_module_objects o
            JOIN winning   w  ON w.id  = o.module_id
            JOIN oe_modules m  ON m.id  = o.module_id
            JOIN chain     ch ON ch.id = m.release_id
            WHERE lower(o.name) = lower({1})
              AND ({2}::text IS NULL OR lower(o.kind) = lower({2}::text))
              AND ({3}::int  IS NULL OR o.object_id   = {3}::int)
            -- Prefer a navigable object (one with source) so go-to-definition
            -- lands somewhere; then base-object kinds ahead of extensions, then
            -- the closest Release, then a stable id tiebreak.
            ORDER BY (o.source_file_id IS NULL), o.kind ASC, ch.depth ASC, o.id ASC
            LIMIT 1
            """;

        var hits = await db.Database
            .SqlQueryRaw<ChainObjectHit>(
                sql,
                seedReleaseId,
                name,
                (object?)kind ?? DBNull.Value,
                (object?)objectId ?? DBNull.Value)
            .ToListAsync(ct);
        return hits.FirstOrDefault();
    }

    /// <summary>
    /// Resolves a member symbol (procedure / field / trigger) named
    /// <paramref name="memberName"/> on the object named
    /// <paramref name="ownerName"/> across the visible chain, returning the
    /// symbol id or <see langword="null"/>.
    /// </summary>
    public static async Task<long?> ResolveMemberSymbolIdAsync(
        AppDbContext db, int seedReleaseId, string ownerName, string memberName, CancellationToken ct)
    {
        const string sql = ReleaseAncestrySql.WinningModules + "\n" + """
            SELECT s.id AS "Id"
            FROM oe_module_symbols s
            JOIN oe_module_objects o  ON o.id  = s.object_id
            JOIN winning           w  ON w.id  = o.module_id
            JOIN oe_modules        m  ON m.id  = o.module_id
            JOIN chain             ch ON ch.id = m.release_id
            WHERE lower(o.name) = lower({1})
              AND lower(s.name) = lower({2})
            ORDER BY s.kind ASC, ch.depth ASC, s.id ASC
            LIMIT 1
            """;

        var hits = await db.Database
            .SqlQueryRaw<ChainIdRow>(sql, seedReleaseId, ownerName, memberName)
            .ToListAsync(ct);
        return hits.Count > 0 ? hits[0].Id : null;
    }
}

/// <summary>One object resolved across the release chain. Column aliases in
/// <see cref="ChainObjectResolution"/>'s SQL map to these property names.</summary>
public sealed record ChainObjectHit(
    long Id,
    long? SourceFileId,
    int LineNumber,
    string Kind,
    string Name,
    int? ObjectId,
    Guid AppId,
    int ModuleId,
    string ModuleName);

/// <summary>Single-id projection for the member-symbol resolution query.</summary>
public sealed record ChainIdRow(long Id);
