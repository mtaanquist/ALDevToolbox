using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddModuleDependencyCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "dependency_count",
                table: "oe_modules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill existing rows from the JSON dependency array length so the
            // objects grid's default order works without a re-import.
            migrationBuilder.Sql(
                "UPDATE oe_modules SET dependency_count = jsonb_array_length(dependencies_json) " +
                "WHERE jsonb_typeof(dependencies_json) = 'array';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "dependency_count",
                table: "oe_modules");
        }
    }
}
