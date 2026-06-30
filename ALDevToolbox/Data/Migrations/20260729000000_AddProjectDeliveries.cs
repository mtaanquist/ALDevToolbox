using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "oe_project_deliveries",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    release_pipeline_id = table.Column<int>(type: "integer", nullable: false),
                    project_build_id = table.Column<int>(type: "integer", nullable: false),
                    triggered_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    environment_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_mode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    schema_sync_mode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    scheduled_for = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    claimed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    failure_message = table.Column<string>(type: "text", nullable: true),
                    diagnostics_log = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_project_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_project_deliveries_oe_project_builds_project_build_id",
                        column: x => x.project_build_id,
                        principalTable: "oe_project_builds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_oe_project_deliveries_oe_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "oe_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_project_deliveries_oe_release_pipelines_release_pipeline~",
                        column: x => x.release_pipeline_id,
                        principalTable: "oe_release_pipelines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_project_deliveries_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_project_deliveries_users_triggered_by_user_id",
                        column: x => x.triggered_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "oe_project_delivery_results",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    project_delivery_id = table.Column<int>(type: "integer", nullable: false),
                    ordering = table.Column<int>(type: "integer", nullable: false),
                    app_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    app_name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    app_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    extension_upload_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    message = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_project_delivery_results", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_project_delivery_results_oe_project_deliveries_project_d~",
                        column: x => x.project_delivery_id,
                        principalTable: "oe_project_deliveries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_project_delivery_results_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_deliveries_organization_id",
                table: "oe_project_deliveries",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_deliveries_pipeline_created",
                table: "oe_project_deliveries",
                columns: new[] { "release_pipeline_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_deliveries_project_build_id",
                table: "oe_project_deliveries",
                column: "project_build_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_deliveries_project_id",
                table: "oe_project_deliveries",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_deliveries_status_scheduled",
                table: "oe_project_deliveries",
                columns: new[] { "status", "scheduled_for" });

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_deliveries_triggered_by_user_id",
                table: "oe_project_deliveries",
                column: "triggered_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_delivery_results_delivery_order",
                table: "oe_project_delivery_results",
                columns: new[] { "project_delivery_id", "ordering" });

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_delivery_results_organization_id",
                table: "oe_project_delivery_results",
                column: "organization_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oe_project_delivery_results");

            migrationBuilder.DropTable(
                name: "oe_project_deliveries");
        }
    }
}
