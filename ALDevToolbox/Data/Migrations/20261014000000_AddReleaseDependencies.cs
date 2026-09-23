using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReleaseDependencies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "oe_release_dependencies",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    release_id = table.Column<int>(type: "integer", nullable: false),
                    dependency_release_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_release_dependencies", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_release_dependencies_oe_releases_dependency_release_id",
                        column: x => x.dependency_release_id,
                        principalTable: "oe_releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_release_dependencies_oe_releases_release_id",
                        column: x => x.release_id,
                        principalTable: "oe_releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_release_dependencies_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_oe_release_dependencies_dependency_release",
                table: "oe_release_dependencies",
                column: "dependency_release_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_release_dependencies_organization_id",
                table: "oe_release_dependencies",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_release_dependencies_release_dependency",
                table: "oe_release_dependencies",
                columns: new[] { "release_id", "dependency_release_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oe_release_dependencies");
        }
    }
}
