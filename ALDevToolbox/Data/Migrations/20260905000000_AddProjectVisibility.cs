using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every existing project becomes Public, which is what it effectively
            // was: the pre-teams rule let everyone in the org read every project.
            // The column default also keeps the invariant true for the new rows a
            // deploy creates before anyone assigns a team.
            migrationBuilder.AddColumn<string>(
                name: "visibility",
                table: "oe_projects",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Public");

            migrationBuilder.CreateTable(
                name: "oe_project_teams",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    team_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_project_teams", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_project_teams_oe_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "oe_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_project_teams_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_project_teams_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_teams_organization_id",
                table: "oe_project_teams",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_teams_project_id_team_id",
                table: "oe_project_teams",
                columns: new[] { "project_id", "team_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_teams_team_id",
                table: "oe_project_teams",
                column: "team_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oe_project_teams");

            migrationBuilder.DropColumn(
                name: "visibility",
                table: "oe_projects");
        }
    }
}
