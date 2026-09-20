using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEnvironmentStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "bc_storage_fetched_at",
                table: "oe_projects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "bc_storage_quota_kb",
                table: "oe_projects",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "bc_database_kb",
                table: "oe_project_environments",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bc_storage_fetched_at",
                table: "oe_projects");

            migrationBuilder.DropColumn(
                name: "bc_storage_quota_kb",
                table: "oe_projects");

            migrationBuilder.DropColumn(
                name: "bc_database_kb",
                table: "oe_project_environments");
        }
    }
}
