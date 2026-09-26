using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlannedUpgrades : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "upgrade_id",
                table: "oe_environment_upgrade_actions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "oe_environment_upgrades",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    target_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    planned_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    created_by = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    closed_by = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_environment_upgrades", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_environment_upgrades_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_environment_upgrades_users_closed_by_user_id",
                        column: x => x.closed_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_oe_environment_upgrades_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "oe_environment_upgrade_lines",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    upgrade_id = table.Column<int>(type: "integer", nullable: false),
                    environment_id = table.Column<int>(type: "integer", nullable: false),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    is_open = table.Column<bool>(type: "boolean", nullable: false),
                    assignee_user_id = table.Column<int>(type: "integer", nullable: true),
                    checked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    checked_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    checked_by = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    added_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_environment_upgrade_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_environment_upgrade_lines_oe_environment_upgrades_upgrad~",
                        column: x => x.upgrade_id,
                        principalTable: "oe_environment_upgrades",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_environment_upgrade_lines_oe_project_environments_enviro~",
                        column: x => x.environment_id,
                        principalTable: "oe_project_environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_environment_upgrade_lines_oe_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "oe_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_environment_upgrade_lines_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_environment_upgrade_lines_users_assignee_user_id",
                        column: x => x.assignee_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_oe_environment_upgrade_lines_users_checked_by_user_id",
                        column: x => x.checked_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_oe_env_upgrade_actions_upgrade",
                table: "oe_environment_upgrade_actions",
                column: "upgrade_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_environment_upgrade_lines_assignee_user_id",
                table: "oe_environment_upgrade_lines",
                column: "assignee_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_environment_upgrade_lines_checked_by_user_id",
                table: "oe_environment_upgrade_lines",
                column: "checked_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_environment_upgrade_lines_environment_id",
                table: "oe_environment_upgrade_lines",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_environment_upgrade_lines_organization_id",
                table: "oe_environment_upgrade_lines",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_environment_upgrade_lines_project_id",
                table: "oe_environment_upgrade_lines",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ux_oe_environment_upgrade_lines_env_open",
                table: "oe_environment_upgrade_lines",
                column: "environment_id",
                unique: true,
                filter: "is_open");

            migrationBuilder.CreateIndex(
                name: "ux_oe_environment_upgrade_lines_upgrade_env",
                table: "oe_environment_upgrade_lines",
                columns: new[] { "upgrade_id", "environment_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oe_environment_upgrades_closed_by_user_id",
                table: "oe_environment_upgrades",
                column: "closed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_environment_upgrades_created_by_user_id",
                table: "oe_environment_upgrades",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_environment_upgrades_org_closed",
                table: "oe_environment_upgrades",
                columns: new[] { "organization_id", "closed_at" });

            migrationBuilder.AddForeignKey(
                name: "FK_oe_environment_upgrade_actions_oe_environment_upgrades_upgr~",
                table: "oe_environment_upgrade_actions",
                column: "upgrade_id",
                principalTable: "oe_environment_upgrades",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oe_environment_upgrade_actions_oe_environment_upgrades_upgr~",
                table: "oe_environment_upgrade_actions");

            migrationBuilder.DropTable(
                name: "oe_environment_upgrade_lines");

            migrationBuilder.DropTable(
                name: "oe_environment_upgrades");

            migrationBuilder.DropIndex(
                name: "ix_oe_env_upgrade_actions_upgrade",
                table: "oe_environment_upgrade_actions");

            migrationBuilder.DropColumn(
                name: "upgrade_id",
                table: "oe_environment_upgrade_actions");
        }
    }
}
