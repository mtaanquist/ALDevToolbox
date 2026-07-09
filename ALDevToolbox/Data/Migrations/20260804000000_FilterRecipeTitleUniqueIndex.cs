using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class FilterRecipeTitleUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_recipes_organization_id_title",
                table: "recipes");

            migrationBuilder.CreateIndex(
                name: "IX_recipes_organization_id_title",
                table: "recipes",
                columns: new[] { "organization_id", "title" },
                unique: true,
                filter: "deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_recipes_organization_id_title",
                table: "recipes");

            migrationBuilder.CreateIndex(
                name: "IX_recipes_organization_id_title",
                table: "recipes",
                columns: new[] { "organization_id", "title" },
                unique: true);
        }
    }
}
