using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeValueAndDownloads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "estimated_value_hours",
                table: "recipes",
                type: "numeric(8,2)",
                precision: 8,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "recipe_downloads",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    recipe_id = table.Column<int>(type: "integer", nullable: false),
                    customer_name = table.Column<string>(type: "text", nullable: false),
                    downloaded_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    downloaded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recipe_downloads", x => x.id);
                    table.ForeignKey(
                        name: "FK_recipe_downloads_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_recipe_downloads_recipes_recipe_id",
                        column: x => x.recipe_id,
                        principalTable: "recipes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_recipe_downloads_users_downloaded_by_user_id",
                        column: x => x.downloaded_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_recipe_downloads_downloaded_by_user_id",
                table: "recipe_downloads",
                column: "downloaded_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_recipe_downloads_organization_id_recipe_id_downloaded_at",
                table: "recipe_downloads",
                columns: new[] { "organization_id", "recipe_id", "downloaded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_recipe_downloads_recipe_id",
                table: "recipe_downloads",
                column: "recipe_id");

            // Keywords moved from space-separated to comma-separated so a
            // multi-word (quoted) tag survives the round-trip. Existing rows
            // hold whitespace-collapsed, single-word tokens, so swapping each
            // space for a comma is a clean 1:1 conversion. See
            // RecipeService.NormaliseKeywords.
            migrationBuilder.Sql("UPDATE recipes SET keywords = replace(keywords, ' ', ',') WHERE keywords <> '';");
            migrationBuilder.Sql("UPDATE recipe_suggestions SET keywords = replace(keywords, ' ', ',') WHERE keywords <> '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse the keyword conversion before dropping the new schema.
            migrationBuilder.Sql("UPDATE recipes SET keywords = replace(keywords, ',', ' ') WHERE keywords <> '';");
            migrationBuilder.Sql("UPDATE recipe_suggestions SET keywords = replace(keywords, ',', ' ') WHERE keywords <> '';");

            migrationBuilder.DropTable(
                name: "recipe_downloads");

            migrationBuilder.DropColumn(
                name: "estimated_value_hours",
                table: "recipes");
        }
    }
}
