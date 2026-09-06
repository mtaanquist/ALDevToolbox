using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class GitHubReleases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "build_pipeline_id",
                table: "oe_release_pipelines",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            // Existing release pipelines all draw from a build pipeline, so they are
            // backfilled with that source rather than with EF's empty string.
            migrationBuilder.AddColumn<string>(
                name: "artifact_source",
                table: "oe_release_pipelines",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "build");

            migrationBuilder.AddColumn<int>(
                name: "github_release_repository_id",
                table: "oe_release_pipelines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "github_release_error",
                table: "oe_project_builds",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "github_release_tag",
                table: "oe_project_builds",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "github_release_url",
                table: "oe_project_builds",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "github_release_repository_id",
                table: "oe_pipelines",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_oe_release_pipelines_github_release_repository_id",
                table: "oe_release_pipelines",
                column: "github_release_repository_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_pipelines_github_release_repository_id",
                table: "oe_pipelines",
                column: "github_release_repository_id");

            migrationBuilder.AddForeignKey(
                name: "FK_oe_pipelines_oe_project_repositories_github_release_reposit~",
                table: "oe_pipelines",
                column: "github_release_repository_id",
                principalTable: "oe_project_repositories",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_oe_release_pipelines_oe_project_repositories_github_release~",
                table: "oe_release_pipelines",
                column: "github_release_repository_id",
                principalTable: "oe_project_repositories",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oe_pipelines_oe_project_repositories_github_release_reposit~",
                table: "oe_pipelines");

            migrationBuilder.DropForeignKey(
                name: "FK_oe_release_pipelines_oe_project_repositories_github_release~",
                table: "oe_release_pipelines");

            migrationBuilder.DropIndex(
                name: "IX_oe_release_pipelines_github_release_repository_id",
                table: "oe_release_pipelines");

            migrationBuilder.DropIndex(
                name: "IX_oe_pipelines_github_release_repository_id",
                table: "oe_pipelines");

            migrationBuilder.DropColumn(
                name: "artifact_source",
                table: "oe_release_pipelines");

            migrationBuilder.DropColumn(
                name: "github_release_repository_id",
                table: "oe_release_pipelines");

            migrationBuilder.DropColumn(
                name: "github_release_error",
                table: "oe_project_builds");

            migrationBuilder.DropColumn(
                name: "github_release_tag",
                table: "oe_project_builds");

            migrationBuilder.DropColumn(
                name: "github_release_url",
                table: "oe_project_builds");

            migrationBuilder.DropColumn(
                name: "github_release_repository_id",
                table: "oe_pipelines");

            migrationBuilder.AlterColumn<int>(
                name: "build_pipeline_id",
                table: "oe_release_pipelines",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
