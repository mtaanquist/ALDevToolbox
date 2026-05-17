using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddModuleReferenceTargetVariableId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "target_variable_id",
                table: "oe_module_references",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_oe_module_references_target_variable",
                table: "oe_module_references",
                column: "target_variable_id",
                filter: "\"target_variable_id\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_oe_module_references_oe_module_variables_target_variable_id",
                table: "oe_module_references",
                column: "target_variable_id",
                principalTable: "oe_module_variables",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oe_module_references_oe_module_variables_target_variable_id",
                table: "oe_module_references");

            migrationBuilder.DropIndex(
                name: "ix_oe_module_references_target_variable",
                table: "oe_module_references");

            migrationBuilder.DropColumn(
                name: "target_variable_id",
                table: "oe_module_references");
        }
    }
}
