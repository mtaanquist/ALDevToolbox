using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubRepositoryCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "github_repository_candidates",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    full_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    html_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    clone_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    default_branch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    app_name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    app_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    app_json_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    discovered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ignored_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ignored_by_user_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_repository_candidates", x => x.id);
                    table.ForeignKey(
                        name: "FK_github_repository_candidates_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_github_repository_candidates_org_full_name",
                table: "github_repository_candidates",
                columns: new[] { "organization_id", "full_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "github_repository_candidates");
        }
    }
}
