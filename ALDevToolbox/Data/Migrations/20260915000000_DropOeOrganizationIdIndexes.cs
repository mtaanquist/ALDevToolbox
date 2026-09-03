using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropOeOrganizationIdIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_oe_module_variables_organization_id",
                table: "oe_module_variables");

            migrationBuilder.DropIndex(
                name: "IX_oe_module_system_references_organization_id",
                table: "oe_module_system_references");

            migrationBuilder.DropIndex(
                name: "IX_oe_module_symbols_organization_id",
                table: "oe_module_symbols");

            migrationBuilder.DropIndex(
                name: "IX_oe_module_references_organization_id",
                table: "oe_module_references");

            migrationBuilder.DropIndex(
                name: "IX_oe_module_objects_organization_id",
                table: "oe_module_objects");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_oe_module_variables_organization_id",
                table: "oe_module_variables",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_module_system_references_organization_id",
                table: "oe_module_system_references",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_module_symbols_organization_id",
                table: "oe_module_symbols",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_module_references_organization_id",
                table: "oe_module_references",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_module_objects_organization_id",
                table: "oe_module_objects",
                column: "organization_id");
        }
    }
}
