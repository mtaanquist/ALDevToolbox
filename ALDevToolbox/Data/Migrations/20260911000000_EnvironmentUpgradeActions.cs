using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnvironmentUpgradeActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "oe_environment_upgrade_actions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    environment_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    requested_by = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    execute_after = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    cancelled_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    cancelled_by = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    cancelled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_environment_upgrade_actions", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_environment_upgrade_actions_oe_project_environments_envi~",
                        column: x => x.environment_id,
                        principalTable: "oe_project_environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_environment_upgrade_actions_oe_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "oe_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_environment_upgrade_actions_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_environment_upgrade_actions_users_cancelled_by_user_id",
                        column: x => x.cancelled_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_oe_environment_upgrade_actions_users_requested_by_user_id",
                        column: x => x.requested_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_oe_env_upgrade_actions_env_requested",
                table: "oe_environment_upgrade_actions",
                columns: new[] { "environment_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_oe_env_upgrade_actions_status_due",
                table: "oe_environment_upgrade_actions",
                columns: new[] { "status", "execute_after" });

            migrationBuilder.CreateIndex(
                name: "IX_oe_environment_upgrade_actions_cancelled_by_user_id",
                table: "oe_environment_upgrade_actions",
                column: "cancelled_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_environment_upgrade_actions_organization_id",
                table: "oe_environment_upgrade_actions",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_environment_upgrade_actions_project_id",
                table: "oe_environment_upgrade_actions",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_environment_upgrade_actions_requested_by_user_id",
                table: "oe_environment_upgrade_actions",
                column: "requested_by_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oe_environment_upgrade_actions");
        }
    }
}
