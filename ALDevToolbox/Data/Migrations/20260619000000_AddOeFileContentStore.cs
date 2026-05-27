using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Moves the inline <c>oe_module_files.content</c> blob into the shared,
    /// content-addressed <c>oe_file_contents</c> store (keyed by SHA-256 hash)
    /// so identical source is stored once instead of once per org. The backfill
    /// rewrites/sorts the whole source corpus and the FK validation + DROP COLUMN
    /// take heavy locks — run this in a maintenance window on a large database.
    /// </summary>
    public partial class AddOeFileContentStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "oe_file_contents",
                columns: table => new
                {
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    content_length = table.Column<int>(type: "integer", nullable: false),
                    line_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_file_contents", x => x.content_hash);
                });

            // Backfill one row per distinct hash BEFORE adding the FK (so it
            // validates) and BEFORE dropping the source column (so we can still
            // read it). length(content) is char count, matching the .NET
            // string.Length the import path stamps into SourceContentLength.
            migrationBuilder.Sql("""
                INSERT INTO oe_file_contents (content_hash, content, content_length, line_count)
                SELECT DISTINCT ON (content_hash) content_hash, content, length(content), line_count
                FROM oe_module_files
                ORDER BY content_hash;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_oe_module_files_content_hash",
                table: "oe_module_files",
                column: "content_hash");

            // Restrict: deleting a file row must never cascade away a blob another
            // file/org references. Orphans are reclaimed on hard-purge.
            migrationBuilder.AddForeignKey(
                name: "FK_oe_module_files_oe_file_contents_content_hash",
                table: "oe_module_files",
                column: "content_hash",
                principalTable: "oe_file_contents",
                principalColumn: "content_hash",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropColumn(
                name: "content",
                table: "oe_module_files");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oe_module_files_oe_file_contents_content_hash",
                table: "oe_module_files");

            migrationBuilder.DropIndex(
                name: "ix_oe_module_files_content_hash",
                table: "oe_module_files");

            // Re-inline the content: add nullable, backfill from the join, then
            // restore the NOT NULL constraint.
            migrationBuilder.AddColumn<string>(
                name: "content",
                table: "oe_module_files",
                type: "text",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE oe_module_files f
                SET content = c.content
                FROM oe_file_contents c
                WHERE f.content_hash = c.content_hash;
                """);

            migrationBuilder.Sql("ALTER TABLE oe_module_files ALTER COLUMN content SET NOT NULL;");

            migrationBuilder.DropTable(
                name: "oe_file_contents");
        }
    }
}
