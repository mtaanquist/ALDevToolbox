using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class PrepareReleaseOnNewBuild : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "prepare_release_on_new_build",
                table: "oe_release_pipelines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "dismiss_reason",
                table: "oe_project_deliveries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "replaced_by_project_build_id",
                table: "oe_project_deliveries",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "prepare_release_on_new_build",
                table: "oe_release_pipelines");

            migrationBuilder.DropColumn(
                name: "dismiss_reason",
                table: "oe_project_deliveries");

            migrationBuilder.DropColumn(
                name: "replaced_by_project_build_id",
                table: "oe_project_deliveries");
        }
    }
}
