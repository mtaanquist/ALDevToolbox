using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPipelineBranchWatching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "branch",
                table: "oe_pipelines",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "oe_repository_branch_heads",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    project_repository_id = table.Column<int>(type: "integer", nullable: false),
                    branch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    head_sha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    pushed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    pusher_login = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    forced = table.Column<bool>(type: "boolean", nullable: false),
                    commit_count = table.Column<int>(type: "integer", nullable: false),
                    commits_json = table.Column<string>(type: "jsonb", nullable: false),
                    is_default_branch = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_repository_branch_heads", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_repository_branch_heads_oe_project_repositories_project_~",
                        column: x => x.project_repository_id,
                        principalTable: "oe_project_repositories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_repository_branch_heads_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oe_repository_merged_pull_requests",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    project_repository_id = table.Column<int>(type: "integer", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    base_branch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    merge_sha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    merged_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    author_login = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_repository_merged_pull_requests", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_repository_merged_pull_requests_oe_project_repositories_~",
                        column: x => x.project_repository_id,
                        principalTable: "oe_project_repositories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_repository_merged_pull_requests_organizations_organizati~",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_oe_repository_branch_heads_organization_id",
                table: "oe_repository_branch_heads",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ux_oe_repository_branch_heads_repo_branch",
                table: "oe_repository_branch_heads",
                columns: new[] { "project_repository_id", "branch" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oe_repository_merged_pull_requests_organization_id",
                table: "oe_repository_merged_pull_requests",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ux_oe_repository_merged_pull_requests_repo_number",
                table: "oe_repository_merged_pull_requests",
                columns: new[] { "project_repository_id", "number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oe_repository_branch_heads");

            migrationBuilder.DropTable(
                name: "oe_repository_merged_pull_requests");

            migrationBuilder.DropColumn(
                name: "branch",
                table: "oe_pipelines");
        }
    }
}
