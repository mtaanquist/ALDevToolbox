using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Adds the Object Explorer symbol index: <c>base_app_symbols</c> holds
    /// one row per procedure / trigger / event publisher / event subscriber
    /// declaration extracted from imported AL files. Powers the "Find
    /// references" gesture in the read-only viewer.
    ///
    /// Also adds <c>symbols_indexed_at</c> on <c>base_app_versions</c> so the
    /// background reindexer can pick up versions that haven't been
    /// extracted yet (either pre-symbol-feature imports or future re-runs
    /// after extractor changes).
    /// </summary>
    public partial class AddSymbolIndex : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "symbols_indexed_at",
                table: "base_app_versions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "base_app_symbols",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    version_id = table.Column<int>(type: "integer", nullable: false),
                    file_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    signature = table.Column<string>(type: "text", nullable: true),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    column_start = table.Column<int>(type: "integer", nullable: false),
                    column_end = table.Column<int>(type: "integer", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_base_app_symbols", x => x.id);
                    table.ForeignKey(
                        name: "FK_base_app_symbols_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_base_app_symbols_base_app_files_file_id",
                        column: x => x.file_id,
                        principalTable: "base_app_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_base_app_symbols_file_line",
                table: "base_app_symbols",
                columns: new[] { "file_id", "line_number" });

            migrationBuilder.CreateIndex(
                name: "IX_base_app_symbols_version_kind_name",
                table: "base_app_symbols",
                columns: new[] { "version_id", "kind", "name" });

            // Case-insensitive lookup for the references query — call sites
            // and declarations can disagree on casing.
            migrationBuilder.Sql(
                "CREATE INDEX ix_base_app_symbols_version_name_lower ON base_app_symbols (version_id, lower(name));");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_base_app_symbols_version_name_lower;");
            migrationBuilder.DropTable("base_app_symbols");
            migrationBuilder.DropColumn(name: "symbols_indexed_at", table: "base_app_versions");
        }
    }
}
