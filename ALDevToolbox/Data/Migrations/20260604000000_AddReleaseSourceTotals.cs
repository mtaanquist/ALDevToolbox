using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Adds <c>source_file_count</c> and <c>source_content_length</c> to
    /// <c>oe_releases</c>. The Releases picker used to recompute both via
    /// correlated subqueries over <c>oe_module_files</c> on every page load
    /// — fine for a single small DVD, painful once an org has imported
    /// several Releases each carrying thousands of files. The file set is
    /// immutable after a Release flips to <c>ready</c>, so a single stamp
    /// at ingest time is enough.
    ///
    /// The Up step also backfills the two columns for any Releases that
    /// were imported before this migration so the picker doesn't render
    /// zeros until the next re-import.
    /// </summary>
    public partial class AddReleaseSourceTotals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "source_content_length",
                table: "oe_releases",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "source_file_count",
                table: "oe_releases",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill existing rows from the live file set. Single round-trip
            // per Release; the aggregate is bounded by oe_module_files which
            // already carries an index on module_id, and oe_modules on
            // release_id.
            migrationBuilder.Sql(@"
                UPDATE oe_releases r
                   SET source_file_count    = COALESCE(t.cnt, 0),
                       source_content_length = COALESCE(t.len, 0)
                  FROM (
                      SELECT m.release_id,
                             COUNT(f.id)                       AS cnt,
                             COALESCE(SUM(LENGTH(f.content)), 0) AS len
                        FROM oe_modules m
                        LEFT JOIN oe_module_files f ON f.module_id = m.id
                       GROUP BY m.release_id
                  ) t
                 WHERE r.id = t.release_id;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source_content_length",
                table: "oe_releases");

            migrationBuilder.DropColumn(
                name: "source_file_count",
                table: "oe_releases");
        }
    }
}
