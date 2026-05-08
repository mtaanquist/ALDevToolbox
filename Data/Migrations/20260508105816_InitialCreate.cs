using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    changed_by = table.Column<string>(type: "TEXT", nullable: false),
                    entity_type = table.Column<string>(type: "TEXT", nullable: false),
                    entity_id = table.Column<int>(type: "INTEGER", nullable: false),
                    action = table.Column<string>(type: "TEXT", nullable: false),
                    snapshot_json = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "modules",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    id_range_size = table.Column<int>(type: "INTEGER", nullable: true),
                    deprecated = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_modules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "runtime_templates",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    runtime = table.Column<int>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    default_application = table.Column<string>(type: "TEXT", nullable: false),
                    default_platform = table.Column<string>(type: "TEXT", nullable: false),
                    defaults_json = table.Column<string>(type: "TEXT", nullable: false),
                    app_source_cop_json = table.Column<string>(type: "TEXT", nullable: false),
                    core_id_range_from = table.Column<int>(type: "INTEGER", nullable: false),
                    core_id_range_to = table.Column<int>(type: "INTEGER", nullable: false),
                    module_id_range_start = table.Column<int>(type: "INTEGER", nullable: false),
                    module_id_range_size = table.Column<int>(type: "INTEGER", nullable: false),
                    deprecated = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "well_known_dependencies",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    dep_id = table.Column<string>(type: "TEXT", nullable: false),
                    dep_name = table.Column<string>(type: "TEXT", nullable: false),
                    dep_publisher = table.Column<string>(type: "TEXT", nullable: false),
                    dep_version_default = table.Column<string>(type: "TEXT", nullable: false),
                    category = table.Column<string>(type: "TEXT", nullable: true),
                    ordering = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_well_known_dependencies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "module_dependencies",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    module_id = table.Column<int>(type: "INTEGER", nullable: false),
                    ordering = table.Column<int>(type: "INTEGER", nullable: false),
                    dep_id = table.Column<string>(type: "TEXT", nullable: false),
                    dep_name = table.Column<string>(type: "TEXT", nullable: false),
                    dep_publisher = table.Column<string>(type: "TEXT", nullable: false),
                    dep_version = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_module_dependencies", x => x.id);
                    table.ForeignKey(
                        name: "FK_module_dependencies_modules_module_id",
                        column: x => x.module_id,
                        principalTable: "modules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "template_folders",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    template_id = table.Column<int>(type: "INTEGER", nullable: false),
                    ordering = table.Column<int>(type: "INTEGER", nullable: false),
                    path = table.Column<string>(type: "TEXT", nullable: false),
                    example_path = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_template_folders", x => x.id);
                    table.ForeignKey(
                        name: "FK_template_folders_runtime_templates_template_id",
                        column: x => x.template_id,
                        principalTable: "runtime_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_entity_timestamp",
                table: "audit_log",
                columns: new[] { "entity_type", "entity_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_module_dependencies_module_id_ordering",
                table: "module_dependencies",
                columns: new[] { "module_id", "ordering" });

            migrationBuilder.CreateIndex(
                name: "IX_modules_key",
                table: "modules",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_runtime_templates_key",
                table: "runtime_templates",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_template_folders_template_id_ordering",
                table: "template_folders",
                columns: new[] { "template_id", "ordering" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "module_dependencies");

            migrationBuilder.DropTable(
                name: "template_folders");

            migrationBuilder.DropTable(
                name: "well_known_dependencies");

            migrationBuilder.DropTable(
                name: "modules");

            migrationBuilder.DropTable(
                name: "runtime_templates");
        }
    }
}
