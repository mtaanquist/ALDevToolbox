using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildArtifactAppId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "app_id",
                table: "oe_project_build_artifacts",
                type: "character varying(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_build_artifacts_app_id",
                table: "oe_project_build_artifacts",
                column: "app_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_oe_project_build_artifacts_app_id",
                table: "oe_project_build_artifacts");

            migrationBuilder.DropColumn(
                name: "app_id",
                table: "oe_project_build_artifacts");
        }
    }
}
