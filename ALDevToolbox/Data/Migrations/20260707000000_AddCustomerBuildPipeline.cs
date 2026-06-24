using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerBuildPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "customer_id",
                table: "oe_import_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "oe_customer_build_results",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    release_id = table.Column<int>(type: "integer", nullable: false),
                    app_name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    app_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    message = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_customer_build_results", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_customer_build_results_oe_releases_release_id",
                        column: x => x.release_id,
                        principalTable: "oe_releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_customer_build_results_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_oe_customer_build_results_organization_id",
                table: "oe_customer_build_results",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_customer_build_results_release",
                table: "oe_customer_build_results",
                column: "release_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oe_customer_build_results");

            migrationBuilder.DropColumn(
                name: "customer_id",
                table: "oe_import_jobs");
        }
    }
}
