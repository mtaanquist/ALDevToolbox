using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFindReferencesModuleTargetNameIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_oe_module_system_references_module_target_name",
                table: "oe_module_system_references",
                columns: new[] { "module_id", "target_object_kind", "target_object_name" },
                filter: "\"target_object_id\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_oe_module_references_module_target_name",
                table: "oe_module_references",
                columns: new[] { "module_id", "target_object_kind", "target_object_name" },
                filter: "\"target_object_id\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_oe_module_system_references_module_target_name",
                table: "oe_module_system_references");

            migrationBuilder.DropIndex(
                name: "ix_oe_module_references_module_target_name",
                table: "oe_module_references");
        }
    }
}
