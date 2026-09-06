using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubRepositoryStandards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "github_repository_ruleset_json",
                table: "organization_settings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "github_repository_standard_files",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    ordering = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_repository_standard_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_github_repository_standard_files_organizations_organization~",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_github_repository_standard_files_organization_id_ordering",
                table: "github_repository_standard_files",
                columns: new[] { "organization_id", "ordering" });

            migrationBuilder.CreateIndex(
                name: "IX_github_repository_standard_files_organization_id_path",
                table: "github_repository_standard_files",
                columns: new[] { "organization_id", "path" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "github_repository_standard_files");

            migrationBuilder.DropColumn(
                name: "github_repository_ruleset_json",
                table: "organization_settings");
        }
    }
}
