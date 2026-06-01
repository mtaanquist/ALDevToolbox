using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCalResolutionIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_oe_module_variables_module_id",
                table: "oe_module_variables");

            migrationBuilder.DropIndex(
                name: "IX_oe_module_references_module_id",
                table: "oe_module_references");

            migrationBuilder.CreateIndex(
                name: "ix_oe_module_variables_module_target",
                table: "oe_module_variables",
                columns: new[] { "module_id", "target_object_kind", "target_object_id" });

            migrationBuilder.CreateIndex(
                name: "ix_oe_module_references_module_target",
                table: "oe_module_references",
                columns: new[] { "module_id", "target_object_kind", "target_object_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_oe_module_variables_module_target",
                table: "oe_module_variables");

            migrationBuilder.DropIndex(
                name: "ix_oe_module_references_module_target",
                table: "oe_module_references");

            migrationBuilder.CreateIndex(
                name: "IX_oe_module_variables_module_id",
                table: "oe_module_variables",
                column: "module_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_module_references_module_id",
                table: "oe_module_references",
                column: "module_id");
        }
    }
}
