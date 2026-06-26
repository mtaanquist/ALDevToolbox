using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddArtifactsBuildModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "auto_build_enabled",
                table: "oe_projects");

            migrationBuilder.AddColumn<int>(
                name: "created_by_user_id",
                table: "oe_projects",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "oe_project_builds",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    started_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    release_id = table.Column<int>(type: "integer", nullable: true),
                    branch = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    bc_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    failure_message = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_project_builds", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_project_builds_oe_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "oe_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_project_builds_oe_releases_release_id",
                        column: x => x.release_id,
                        principalTable: "oe_releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_oe_project_builds_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_project_builds_users_started_by_user_id",
                        column: x => x.started_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "oe_project_build_artifacts",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    project_build_id = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    app_name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    app_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    runtime_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_project_build_artifacts", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_project_build_artifacts_oe_project_builds_project_build_~",
                        column: x => x.project_build_id,
                        principalTable: "oe_project_builds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_project_build_artifacts_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oe_project_build_commits",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    project_build_id = table.Column<int>(type: "integer", nullable: false),
                    project_repository_id = table.Column<int>(type: "integer", nullable: true),
                    short_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    author = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    committed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ordering = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_project_build_commits", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_project_build_commits_oe_project_builds_project_build_id",
                        column: x => x.project_build_id,
                        principalTable: "oe_project_builds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_project_build_commits_oe_project_repositories_project_re~",
                        column: x => x.project_repository_id,
                        principalTable: "oe_project_repositories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_oe_project_build_commits_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oe_project_build_logs",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    project_build_id = table.Column<int>(type: "integer", nullable: false),
                    project_repository_id = table.Column<int>(type: "integer", nullable: true),
                    section = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    ordering = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_project_build_logs", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_project_build_logs_oe_project_builds_project_build_id",
                        column: x => x.project_build_id,
                        principalTable: "oe_project_builds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_project_build_logs_oe_project_repositories_project_repos~",
                        column: x => x.project_repository_id,
                        principalTable: "oe_project_repositories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_oe_project_build_logs_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oe_project_build_repo_commits",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    project_build_id = table.Column<int>(type: "integer", nullable: false),
                    project_repository_id = table.Column<int>(type: "integer", nullable: true),
                    repo_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    repo_display_name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    commit_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    committed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_project_build_repo_commits", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_project_build_repo_commits_oe_project_builds_project_bui~",
                        column: x => x.project_build_id,
                        principalTable: "oe_project_builds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_project_build_repo_commits_oe_project_repositories_proje~",
                        column: x => x.project_repository_id,
                        principalTable: "oe_project_repositories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_oe_project_build_repo_commits_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_oe_projects_created_by_user_id",
                table: "oe_projects",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_build_artifacts_build",
                table: "oe_project_build_artifacts",
                column: "project_build_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_build_artifacts_organization_id",
                table: "oe_project_build_artifacts",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_build_commits_build",
                table: "oe_project_build_commits",
                column: "project_build_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_build_commits_organization_id",
                table: "oe_project_build_commits",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_build_commits_project_repository_id",
                table: "oe_project_build_commits",
                column: "project_repository_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_build_logs_build",
                table: "oe_project_build_logs",
                column: "project_build_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_build_logs_organization_id",
                table: "oe_project_build_logs",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_build_logs_project_repository_id",
                table: "oe_project_build_logs",
                column: "project_repository_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_build_repo_commits_build",
                table: "oe_project_build_repo_commits",
                column: "project_build_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_build_repo_commits_organization_id",
                table: "oe_project_build_repo_commits",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_build_repo_commits_project_repository_id",
                table: "oe_project_build_repo_commits",
                column: "project_repository_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_builds_organization_id",
                table: "oe_project_builds",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_builds_project_started",
                table: "oe_project_builds",
                columns: new[] { "project_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_builds_release",
                table: "oe_project_builds",
                column: "release_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_builds_started_by_user_id",
                table: "oe_project_builds",
                column: "started_by_user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_oe_projects_users_created_by_user_id",
                table: "oe_projects",
                column: "created_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oe_projects_users_created_by_user_id",
                table: "oe_projects");

            migrationBuilder.DropTable(
                name: "oe_project_build_artifacts");

            migrationBuilder.DropTable(
                name: "oe_project_build_commits");

            migrationBuilder.DropTable(
                name: "oe_project_build_logs");

            migrationBuilder.DropTable(
                name: "oe_project_build_repo_commits");

            migrationBuilder.DropTable(
                name: "oe_project_builds");

            migrationBuilder.DropIndex(
                name: "IX_oe_projects_created_by_user_id",
                table: "oe_projects");

            migrationBuilder.DropColumn(
                name: "created_by_user_id",
                table: "oe_projects");

            migrationBuilder.AddColumn<bool>(
                name: "auto_build_enabled",
                table: "oe_projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
