using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Removes the redundant <c>minor</c> column from <c>base_app_versions</c>.
    /// BC's version field is <c>major.cu.x.y</c> — there's no separate minor —
    /// so the original three-column key duplicated information. The unique
    /// index moves from (organization_id, major, minor, cumulative_update) to
    /// (organization_id, major, cumulative_update).
    /// </summary>
    public partial class DropBaseAppVersionMinor : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop the old filtered unique index. EF's snake_case naming and
            // the raw `CREATE UNIQUE INDEX` we ran in AddObjectExplorer chose
            // the same identifier, so a single DROP catches it.
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_base_app_versions_org_major_minor_cu\";");

            migrationBuilder.DropColumn(
                name: "minor",
                table: "base_app_versions");

            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IX_base_app_versions_org_major_cu " +
                "ON base_app_versions (organization_id, major, cumulative_update) " +
                "WHERE deleted_at IS NULL;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_base_app_versions_org_major_cu\";");

            migrationBuilder.AddColumn<int>(
                name: "minor",
                table: "base_app_versions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_base_app_versions_org_major_minor_cu\" " +
                "ON base_app_versions (organization_id, major, minor, cumulative_update) " +
                "WHERE deleted_at IS NULL;");
        }
    }
}
