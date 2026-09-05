using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class GitHubPullRequestChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "github_webhook_secret_encrypted",
                table: "system_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "check_run_id",
                table: "oe_project_builds",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "head_sha",
                table: "oe_project_builds",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "pull_request_number",
                table: "oe_project_builds",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "trigger",
                table: "oe_project_builds",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "manual");

            migrationBuilder.CreateTable(
                name: "oe_project_build_diagnostics",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    project_build_id = table.Column<int>(type: "integer", nullable: false),
                    project_repository_id = table.Column<int>(type: "integer", nullable: true),
                    path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    line = table.Column<int>(type: "integer", nullable: false),
                    column = table.Column<int>(type: "integer", nullable: false),
                    severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    ordering = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_project_build_diagnostics", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_project_build_diagnostics_oe_project_builds_project_buil~",
                        column: x => x.project_build_id,
                        principalTable: "oe_project_builds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_project_build_diagnostics_oe_project_repositories_projec~",
                        column: x => x.project_repository_id,
                        principalTable: "oe_project_repositories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_oe_project_build_diagnostics_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_build_diagnostics_build_ordering",
                table: "oe_project_build_diagnostics",
                columns: new[] { "project_build_id", "ordering" });

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_build_diagnostics_organization_id",
                table: "oe_project_build_diagnostics",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_build_diagnostics_project_repository_id",
                table: "oe_project_build_diagnostics",
                column: "project_repository_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oe_project_build_diagnostics");

            migrationBuilder.DropColumn(
                name: "github_webhook_secret_encrypted",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "check_run_id",
                table: "oe_project_builds");

            migrationBuilder.DropColumn(
                name: "head_sha",
                table: "oe_project_builds");

            migrationBuilder.DropColumn(
                name: "pull_request_number",
                table: "oe_project_builds");

            migrationBuilder.DropColumn(
                name: "trigger",
                table: "oe_project_builds");
        }
    }
}
