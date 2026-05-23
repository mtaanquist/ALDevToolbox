namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Shared recursive-CTE fragments for the release-ancestry chain. Every
/// find-references / dependency / interface-implementer query walks the same
/// chain and resolves each AppId to the module at the smallest depth (closest
/// to the current release) — that "winning" selection is the shadowing rule: a
/// child release sees an ancestor's module only when it doesn't ship its own
/// copy. Defining the chain + winning CTEs once keeps the queries — split
/// across <see cref="ObjectExplorerService"/> (interface-implementer outline)
/// and <see cref="ReferenceQueryService"/> (find-references / dependencies) —
/// from drifting. <c>{0}</c> is the seed release id, and each query appends
/// its own SELECT tail. <see cref="WinningModules"/> omits <c>m.name</c>;
/// <see cref="WinningModulesWithName"/> includes it for queries that report
/// the module.
/// </summary>
internal static class ReleaseAncestrySql
{
    public const string Chain = """
        WITH RECURSIVE chain AS (
            SELECT id, parent_release_id, 0 AS depth
            FROM oe_releases
            WHERE id = {0}
            UNION ALL
            SELECT r.id, r.parent_release_id, c.depth + 1
            FROM oe_releases r
            JOIN chain c ON r.id = c.parent_release_id
        )
        """;

    public const string WinningModules = Chain + """
        ,
        winning AS (
            SELECT DISTINCT ON (m.app_id) m.id, m.app_id
            FROM oe_modules m
            JOIN chain c ON c.id = m.release_id
            ORDER BY m.app_id, c.depth ASC
        )
        """;

    public const string WinningModulesWithName = Chain + """
        ,
        winning AS (
            SELECT DISTINCT ON (m.app_id) m.id, m.app_id, m.name
            FROM oe_modules m
            JOIN chain c ON c.id = m.release_id
            ORDER BY m.app_id, c.depth ASC
        )
        """;
}
