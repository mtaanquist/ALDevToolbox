using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddModuleSystemReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "oe_module_system_references",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    module_id = table.Column<long>(type: "bigint", nullable: false),
                    source_object_id = table.Column<long>(type: "bigint", nullable: false),
                    target_app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_object_kind = table.Column<string>(type: "text", nullable: false),
                    target_object_id = table.Column<int>(type: "integer", nullable: true),
                    target_object_name = table.Column<string>(type: "text", nullable: false),
                    system_method_name = table.Column<string>(type: "text", nullable: false),
                    reference_kind = table.Column<string>(type: "text", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: true),
                    column_number = table.Column<int>(type: "integer", nullable: true),
                    source_symbol_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_module_system_references", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_module_system_references_oe_module_objects_source_object~",
                        column: x => x.source_object_id,
                        principalTable: "oe_module_objects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_module_system_references_oe_module_symbols_source_symbol~",
                        column: x => x.source_symbol_id,
                        principalTable: "oe_module_symbols",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_oe_module_system_references_oe_modules_module_id",
                        column: x => x.module_id,
                        principalTable: "oe_modules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_module_system_references_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_oe_module_system_references_module_target",
                table: "oe_module_system_references",
                columns: new[] { "module_id", "target_object_kind", "target_object_id" });

            migrationBuilder.CreateIndex(
                name: "IX_oe_module_system_references_organization_id",
                table: "oe_module_system_references",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_module_system_references_source_object",
                table: "oe_module_system_references",
                column: "source_object_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_module_system_references_source_symbol_id",
                table: "oe_module_system_references",
                column: "source_symbol_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_module_system_references_target_id",
                table: "oe_module_system_references",
                columns: new[] { "target_app_id", "target_object_kind", "target_object_id" });

            migrationBuilder.CreateIndex(
                name: "ix_oe_module_system_references_target_name",
                table: "oe_module_system_references",
                columns: new[] { "target_app_id", "target_object_kind", "target_object_name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oe_module_system_references");
        }
    }
}
