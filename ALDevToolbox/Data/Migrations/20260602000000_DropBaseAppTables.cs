using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Drops the legacy <c>base_app_*</c> schema now that the Object Explorer
    /// milestone has migrated every read and write path to the <c>oe_*</c>
    /// tables introduced in <c>20260601000000_AddObjectExplorerTables</c>.
    ///
    /// The drop is intentionally simple — <c>CASCADE</c> on each table also
    /// drops dependent FKs and indexes. Down throws: re-creating the legacy
    /// schema after every consumer has been deleted would require restoring a
    /// nontrivial slice of code, which is squarely PR-revert territory rather
    /// than a migration-rollback one.
    ///
    /// See <c>.design/migration-history.md</c> for the milestone arc and
    /// <c>.design/object-explorer.md</c> for the new model.
    /// </summary>
    public partial class DropBaseAppTables : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Order matters even with CASCADE so the lineage is readable: dependents
            // first, principals last. Postgres would resolve the chain either way.
            migrationBuilder.Sql("DROP TABLE IF EXISTS base_app_symbols CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS base_app_files CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS base_app_extensions CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS base_app_versions CASCADE;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException(
                "DropBaseAppTables is forward-only: the legacy base_app_* schema and its consuming "
                + "code were deleted in the same milestone, so a rollback would require restoring a "
                + "significant amount of source — use 'git revert' on the merge commit instead.");
        }
    }
}
