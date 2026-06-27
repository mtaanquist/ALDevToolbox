using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPipelines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "pipeline_id",
                table: "oe_project_builds",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "oe_pipelines",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    created_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    requested_app_ids_json = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_pipelines", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_pipelines_oe_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "oe_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_pipelines_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_pipelines_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_builds_pipeline_started",
                table: "oe_project_builds",
                columns: new[] { "pipeline_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "IX_oe_pipelines_created_by_user_id",
                table: "oe_pipelines",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_pipelines_organization_id",
                table: "oe_pipelines",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_pipelines_project",
                table: "oe_pipelines",
                column: "project_id");

            migrationBuilder.AddForeignKey(
                name: "FK_oe_project_builds_oe_pipelines_pipeline_id",
                table: "oe_project_builds",
                column: "pipeline_id",
                principalTable: "oe_pipelines",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            // Per-project name uniqueness on active rows, case-insensitive — a
            // functional index EF can't model, mirroring ix_oe_projects_org_name_active.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_oe_pipelines_project_name_active " +
                "ON oe_pipelines (project_id, lower(name)) " +
                "WHERE deleted_at IS NULL;");

            // Backfill: every project gets a "Default" pipeline (null selection =
            // build everything, preserving today's behaviour), and existing builds
            // re-parent onto it. Idempotent — guarded so a re-run is a no-op.
            migrationBuilder.Sql(BackfillSql);
        }

        /// <summary>
        /// Idempotent data backfill for the Project -> Pipeline -> Build split. See
        /// <c>.design/artifacts.md</c>. Exposed for the migration test.
        /// </summary>
        public const string BackfillSql = @"
            -- 1. A 'Default' pipeline per project that has none yet (owned by the
            --    project's owner; null selection = build all).
            INSERT INTO oe_pipelines
                (organization_id, project_id, created_by_user_id, name, requested_app_ids_json, created_at, updated_at, deleted_at)
            SELECT p.organization_id, p.id, p.created_by_user_id, 'Default', NULL, now(), now(), NULL
            FROM oe_projects p
            WHERE NOT EXISTS (SELECT 1 FROM oe_pipelines pl WHERE pl.project_id = p.id);

            -- 2. Re-parent existing builds onto their project's Default pipeline.
            UPDATE oe_project_builds b
            SET pipeline_id = pl.id
            FROM oe_pipelines pl
            WHERE b.pipeline_id IS NULL
              AND pl.project_id = b.project_id
              AND pl.name = 'Default'
              AND pl.deleted_at IS NULL;
        ";

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oe_project_builds_oe_pipelines_pipeline_id",
                table: "oe_project_builds");

            migrationBuilder.DropTable(
                name: "oe_pipelines");

            migrationBuilder.DropIndex(
                name: "ix_oe_project_builds_pipeline_started",
                table: "oe_project_builds");

            migrationBuilder.DropColumn(
                name: "pipeline_id",
                table: "oe_project_builds");
        }
    }
}
