using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class MergeProjectKindAndAddCal : Migration
    {
        // Data-only restructure of the Release kind discriminator (no schema
        // change — the columns stay as they are, values just move):
        //
        //   1. Releases imported through the C/AL TXT path become kind = 'cal'.
        //      Identified via their persisted oe_import_jobs rows (kind =
        //      'cal_txt'); job rows are never deleted by code, so coverage is
        //      complete unless an operator hand-pruned the table — such a
        //      release simply stays third_party, which is harmless.
        //   2. Manually imported 'project' releases merge into 'third_party'
        //      (the publisher now differentiates ISV apps from per-customer
        //      bundles). Pipeline-build releases — the ones an oe_project_builds
        //      row points at — keep kind = 'project'; that value is now
        //      reserved for them. project_name is kept in place (hidden, not
        //      wiped) and copied into an empty publisher so the Third-party
        //      tab's publisher grouping has something to group by.
        //
        // Step 1 runs first so a C/AL release that was historically imported as
        // 'project' lands on 'cal', not in the third_party merge.
        // See .design/object-explorer.md.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE oe_releases r SET kind = 'cal' " +
                "WHERE EXISTS (SELECT 1 FROM oe_import_jobs j " +
                "              WHERE j.release_id = r.id AND j.kind = 'cal_txt');");

            migrationBuilder.Sql(
                "UPDATE oe_releases r " +
                "SET kind = 'third_party', " +
                "    publisher = CASE WHEN COALESCE(r.publisher, '') = '' " +
                "                     THEN r.project_name ELSE r.publisher END " +
                "WHERE r.kind = 'project' " +
                "  AND NOT EXISTS (SELECT 1 FROM oe_project_builds b " +
                "                  WHERE b.release_id = r.id);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Best-effort: the C/AL flip reverses cleanly via the same job rows;
            // the project → third_party merge is lossy (we can't tell a merged
            // release from an always-third_party one) and is not reversed.
            migrationBuilder.Sql(
                "UPDATE oe_releases r SET kind = 'third_party' " +
                "WHERE r.kind = 'cal' " +
                "  AND EXISTS (SELECT 1 FROM oe_import_jobs j " +
                "              WHERE j.release_id = r.id AND j.kind = 'cal_txt');");
        }
    }
}
