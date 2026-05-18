using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddModuleVariablePositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "column_end",
                table: "oe_module_variables",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "column_start",
                table: "oe_module_variables",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "line_number",
                table: "oe_module_variables",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "column_end",
                table: "oe_module_variables");

            migrationBuilder.DropColumn(
                name: "column_start",
                table: "oe_module_variables");

            migrationBuilder.DropColumn(
                name: "line_number",
                table: "oe_module_variables");
        }
    }
}
