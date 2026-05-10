using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Adds the <c>runtime_template_default_modules</c> join table introduced in
    /// Milestone P2.1 — pre-selected modules per template. Ordering is admin
    /// declared and surfaces only as the visual order of ticked checkboxes on
    /// the New Workspace form. Cascade-delete on either side keeps the join
    /// row from outliving its template or its module.
    /// </summary>
    public partial class AddRuntimeTemplateDefaultModules : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "runtime_template_default_modules",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    runtime_template_id = table.Column<int>(type: "INTEGER", nullable: false),
                    module_id = table.Column<int>(type: "INTEGER", nullable: false),
                    ordering = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_template_default_modules", x => x.id);
                    table.ForeignKey(
                        name: "FK_runtime_template_default_modules_runtime_templates_runtime_template_id",
                        column: x => x.runtime_template_id,
                        principalTable: "runtime_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_runtime_template_default_modules_modules_module_id",
                        column: x => x.module_id,
                        principalTable: "modules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_runtime_template_default_modules_runtime_template_id_ordering",
                table: "runtime_template_default_modules",
                columns: new[] { "runtime_template_id", "ordering" });

            migrationBuilder.CreateIndex(
                name: "IX_runtime_template_default_modules_runtime_template_id_module_id",
                table: "runtime_template_default_modules",
                columns: new[] { "runtime_template_id", "module_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_runtime_template_default_modules_module_id",
                table: "runtime_template_default_modules",
                column: "module_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "runtime_template_default_modules");
        }
    }
}
