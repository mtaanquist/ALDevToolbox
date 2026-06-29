using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReleasePipelines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "oe_release_pipelines",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    created_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    build_pipeline_id = table.Column<int>(type: "integer", nullable: false),
                    project_environment_id = table.Column<int>(type: "integer", nullable: false),
                    version_mode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    schema_sync_mode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_release_pipelines", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_release_pipelines_oe_pipelines_build_pipeline_id",
                        column: x => x.build_pipeline_id,
                        principalTable: "oe_pipelines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_oe_release_pipelines_oe_project_environments_project_enviro~",
                        column: x => x.project_environment_id,
                        principalTable: "oe_project_environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_oe_release_pipelines_oe_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "oe_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_release_pipelines_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_release_pipelines_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_oe_release_pipelines_build_pipeline",
                table: "oe_release_pipelines",
                column: "build_pipeline_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_release_pipelines_created_by_user_id",
                table: "oe_release_pipelines",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_release_pipelines_environment",
                table: "oe_release_pipelines",
                column: "project_environment_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_release_pipelines_organization_id",
                table: "oe_release_pipelines",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_release_pipelines_project",
                table: "oe_release_pipelines",
                column: "project_id");

            // Per-project name uniqueness on active rows, case-insensitive — a
            // functional index EF can't model, mirroring ix_oe_pipelines_project_name_active.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_oe_release_pipelines_project_name_active " +
                "ON oe_release_pipelines (project_id, lower(name)) " +
                "WHERE deleted_at IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oe_release_pipelines");
        }
    }
}
