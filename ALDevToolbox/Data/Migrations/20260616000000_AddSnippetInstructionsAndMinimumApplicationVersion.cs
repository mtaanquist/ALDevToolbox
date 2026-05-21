using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSnippetInstructionsAndMinimumApplicationVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "instructions",
                table: "snippets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "minimum_application_version_id",
                table: "snippets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "instructions",
                table: "snippet_suggestions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "minimum_application_version_id",
                table: "snippet_suggestions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_snippets_minimum_application_version_id",
                table: "snippets",
                column: "minimum_application_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_snippet_suggestions_minimum_application_version_id",
                table: "snippet_suggestions",
                column: "minimum_application_version_id");

            migrationBuilder.AddForeignKey(
                name: "FK_snippet_suggestions_application_versions_minimum_applicatio~",
                table: "snippet_suggestions",
                column: "minimum_application_version_id",
                principalTable: "application_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_snippets_application_versions_minimum_application_version_id",
                table: "snippets",
                column: "minimum_application_version_id",
                principalTable: "application_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_snippet_suggestions_application_versions_minimum_applicatio~",
                table: "snippet_suggestions");

            migrationBuilder.DropForeignKey(
                name: "FK_snippets_application_versions_minimum_application_version_id",
                table: "snippets");

            migrationBuilder.DropIndex(
                name: "IX_snippets_minimum_application_version_id",
                table: "snippets");

            migrationBuilder.DropIndex(
                name: "IX_snippet_suggestions_minimum_application_version_id",
                table: "snippet_suggestions");

            migrationBuilder.DropColumn(
                name: "instructions",
                table: "snippets");

            migrationBuilder.DropColumn(
                name: "minimum_application_version_id",
                table: "snippets");

            migrationBuilder.DropColumn(
                name: "instructions",
                table: "snippet_suggestions");

            migrationBuilder.DropColumn(
                name: "minimum_application_version_id",
                table: "snippet_suggestions");
        }
    }
}
