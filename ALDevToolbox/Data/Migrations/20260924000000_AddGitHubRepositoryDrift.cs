using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubRepositoryDrift : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "github_repository_drift",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    repository = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    field = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    current = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    proposed = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    release_id = table.Column<int>(type: "integer", nullable: false),
                    detected_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_repository_drift", x => x.id);
                    table.ForeignKey(
                        name: "FK_github_repository_drift_oe_releases_release_id",
                        column: x => x.release_id,
                        principalTable: "oe_releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_github_repository_drift_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_github_repository_drift_release_id",
                table: "github_repository_drift",
                column: "release_id");

            migrationBuilder.CreateIndex(
                name: "ux_github_repository_drift_org_repo_path_field",
                table: "github_repository_drift",
                columns: new[] { "organization_id", "repository", "path", "field" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "github_repository_drift");
        }
    }
}
