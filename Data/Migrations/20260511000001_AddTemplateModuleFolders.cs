using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Splits per-extension folder scaffolding into two scopes: <c>template_folders</c>
    /// (Core-only) keeps the existing rows; <c>template_module_folders</c> +
    /// <c>template_module_files</c> are new and emit into module extensions only.
    /// Nothing is back-filled — the previous behaviour duplicated Core's folders
    /// into every module ZIP, which is the bug this split fixes. Existing
    /// templates start with an empty module-folder list so module ZIPs ship
    /// with just their app.json + AppSourceCop.json + the static fallback
    /// folders (libs, permissionsets, Translations) until an admin opts in.
    /// </summary>
    public partial class AddTemplateModuleFolders : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "template_module_folders",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    template_id = table.Column<int>(type: "INTEGER", nullable: false),
                    ordering = table.Column<int>(type: "INTEGER", nullable: false),
                    path = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_template_module_folders", x => x.id);
                    table.ForeignKey(
                        name: "FK_template_module_folders_runtime_templates_template_id",
                        column: x => x.template_id,
                        principalTable: "runtime_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_template_module_folders_template_id_ordering",
                table: "template_module_folders",
                columns: new[] { "template_id", "ordering" });

            migrationBuilder.CreateTable(
                name: "template_module_files",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    template_module_folder_id = table.Column<int>(type: "INTEGER", nullable: false),
                    ordering = table.Column<int>(type: "INTEGER", nullable: false),
                    path = table.Column<string>(type: "TEXT", nullable: false),
                    content = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_template_module_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_template_module_files_template_module_folders_template_module_folder_id",
                        column: x => x.template_module_folder_id,
                        principalTable: "template_module_folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_template_module_files_template_module_folder_id_ordering",
                table: "template_module_files",
                columns: new[] { "template_module_folder_id", "ordering" });

            migrationBuilder.CreateIndex(
                name: "IX_template_module_files_template_module_folder_id_path",
                table: "template_module_files",
                columns: new[] { "template_module_folder_id", "path" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "template_module_files");

            migrationBuilder.DropTable(
                name: "template_module_folders");
        }
    }
}
