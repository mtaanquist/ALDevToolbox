using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeDownloadProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "project_id",
                table: "recipe_downloads",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_recipe_downloads_project_id",
                table: "recipe_downloads",
                column: "project_id");

            migrationBuilder.AddForeignKey(
                name: "FK_recipe_downloads_oe_projects_project_id",
                table: "recipe_downloads",
                column: "project_id",
                principalTable: "oe_projects",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_recipe_downloads_oe_projects_project_id",
                table: "recipe_downloads");

            migrationBuilder.DropIndex(
                name: "IX_recipe_downloads_project_id",
                table: "recipe_downloads");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "recipe_downloads");
        }
    }
}
