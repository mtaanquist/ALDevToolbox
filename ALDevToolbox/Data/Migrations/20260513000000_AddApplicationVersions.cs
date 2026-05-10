using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Adds the curated <c>application_versions</c> catalogue introduced in
    /// Milestone P2.4 — replaces free-text Application Version + Runtime inputs
    /// on the builder forms with a single select fed by this table. A nullable
    /// <c>default_application_version_id</c> column on <c>runtime_templates</c>
    /// preselects an entry per template; <c>SetNull</c> on delete keeps templates
    /// alive when an entry is removed (the existing <c>default_application</c> /
    /// <c>runtime</c> string columns then act as orphan fallbacks).
    /// </summary>
    public partial class AddApplicationVersions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "application_versions",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    application = table.Column<string>(type: "TEXT", nullable: false),
                    runtime = table.Column<string>(type: "TEXT", nullable: false),
                    ordering = table.Column<int>(type: "INTEGER", nullable: false),
                    deprecated = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<System.DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<System.DateTime>(type: "TEXT", nullable: false),
                    deleted_at = table.Column<System.DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_versions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_application_versions_key",
                table: "application_versions",
                column: "key",
                unique: true);

            migrationBuilder.AddColumn<int>(
                name: "default_application_version_id",
                table: "runtime_templates",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_runtime_templates_default_application_version_id",
                table: "runtime_templates",
                column: "default_application_version_id");

            migrationBuilder.AddForeignKey(
                name: "FK_runtime_templates_application_versions_default_application_version_id",
                table: "runtime_templates",
                column: "default_application_version_id",
                principalTable: "application_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_runtime_templates_application_versions_default_application_version_id",
                table: "runtime_templates");

            migrationBuilder.DropIndex(
                name: "IX_runtime_templates_default_application_version_id",
                table: "runtime_templates");

            migrationBuilder.DropColumn(
                name: "default_application_version_id",
                table: "runtime_templates");

            migrationBuilder.DropTable(
                name: "application_versions");
        }
    }
}
