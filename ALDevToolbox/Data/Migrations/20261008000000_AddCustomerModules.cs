using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerModules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_modules",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    publisher = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    app_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_modules", x => x.id);
                    table.ForeignKey(
                        name: "FK_customer_modules_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oe_environment_apps",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    environment_id = table.Column<int>(type: "integer", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    publisher = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    fetched_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_environment_apps", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_environment_apps_oe_project_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "oe_project_environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_environment_apps_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oe_project_modules",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    module_id = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    note = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_project_modules", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_project_modules_customer_modules_module_id",
                        column: x => x.module_id,
                        principalTable: "customer_modules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_project_modules_oe_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "oe_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_project_modules_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_modules_organization_id_app_id",
                table: "customer_modules",
                columns: new[] { "organization_id", "app_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_modules_organization_id_name",
                table: "customer_modules",
                columns: new[] { "organization_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oe_environment_apps_app_id",
                table: "oe_environment_apps",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_environment_apps_environment_id_app_id",
                table: "oe_environment_apps",
                columns: new[] { "environment_id", "app_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oe_environment_apps_organization_id",
                table: "oe_environment_apps",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_modules_module_id",
                table: "oe_project_modules",
                column: "module_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_modules_organization_id",
                table: "oe_project_modules",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_modules_project_id_module_id",
                table: "oe_project_modules",
                columns: new[] { "project_id", "module_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oe_environment_apps");

            migrationBuilder.DropTable(
                name: "oe_project_modules");

            migrationBuilder.DropTable(
                name: "customer_modules");
        }
    }
}
