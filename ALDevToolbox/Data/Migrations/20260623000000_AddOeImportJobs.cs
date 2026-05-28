using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOeImportJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "oe_import_jobs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    release_id = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<int>(type: "integer", nullable: true),
                    is_site_admin = table.Column<bool>(type: "boolean", nullable: false),
                    is_system_organization = table.Column<bool>(type: "boolean", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    download_url = table.Column<string>(type: "text", nullable: true),
                    staged_zip_path = table.Column<string>(type: "text", nullable: true),
                    staged_is_dvd = table.Column<bool>(type: "boolean", nullable: true),
                    store_symbol_reference = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_import_jobs", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_import_jobs_oe_releases_release_id",
                        column: x => x.release_id,
                        principalTable: "oe_releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_import_jobs_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_import_jobs_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_oe_import_jobs_org_created",
                table: "oe_import_jobs",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_oe_import_jobs_release_id",
                table: "oe_import_jobs",
                column: "release_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_import_jobs_status",
                table: "oe_import_jobs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_oe_import_jobs_user_id",
                table: "oe_import_jobs",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oe_import_jobs");
        }
    }
}
