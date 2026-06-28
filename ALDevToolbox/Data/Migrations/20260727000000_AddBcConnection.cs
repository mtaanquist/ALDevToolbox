using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBcConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bc_client_id",
                table: "oe_projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bc_client_secret_encrypted",
                table: "oe_projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "bc_client_secret_expires_at",
                table: "oe_projects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "bc_connection_verified_at",
                table: "oe_projects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "bc_credentials_updated_at",
                table: "oe_projects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "bc_tenant_id",
                table: "oe_projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bc_time_zone",
                table: "oe_projects",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "oe_project_environments",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    company_name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    fetched_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    missing_since = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_project_environments", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_project_environments_oe_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "oe_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_project_environments_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_environments_organization_id",
                table: "oe_project_environments",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_environments_project_name",
                table: "oe_project_environments",
                columns: new[] { "project_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "bc_client_id",
                table: "oe_projects");

            migrationBuilder.DropColumn(
                name: "bc_client_secret_encrypted",
                table: "oe_projects");

            migrationBuilder.DropColumn(
                name: "bc_client_secret_expires_at",
                table: "oe_projects");

            migrationBuilder.DropColumn(
                name: "bc_connection_verified_at",
                table: "oe_projects");

            migrationBuilder.DropColumn(
                name: "bc_credentials_updated_at",
                table: "oe_projects");

            migrationBuilder.DropColumn(
                name: "bc_tenant_id",
                table: "oe_projects");

            migrationBuilder.DropColumn(
                name: "bc_time_zone",
                table: "oe_projects");
        }
    }
}
