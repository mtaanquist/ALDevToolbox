using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropAppKeyedSystemReferenceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_oe_module_system_references_target_id",
                table: "oe_module_system_references");

            migrationBuilder.DropIndex(
                name: "ix_oe_module_system_references_target_name",
                table: "oe_module_system_references");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_oe_module_system_references_target_id",
                table: "oe_module_system_references",
                columns: new[] { "target_app_id", "target_object_kind", "target_object_id" });

            migrationBuilder.CreateIndex(
                name: "ix_oe_module_system_references_target_name",
                table: "oe_module_system_references",
                columns: new[] { "target_app_id", "target_object_kind", "target_object_name" });
        }
    }
}
