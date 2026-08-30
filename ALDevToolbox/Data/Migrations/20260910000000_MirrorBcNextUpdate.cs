using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class MirrorBcNextUpdate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "bc_next_update_date",
                table: "oe_project_environments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "bc_next_update_fetched_at",
                table: "oe_project_environments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "bc_next_update_ignores_window",
                table: "oe_project_environments",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "bc_next_update_latest_date",
                table: "oe_project_environments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bc_next_update_status",
                table: "oe_project_environments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bc_next_update_type",
                table: "oe_project_environments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bc_next_update_version",
                table: "oe_project_environments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bc_next_update_date",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "bc_next_update_fetched_at",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "bc_next_update_ignores_window",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "bc_next_update_latest_date",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "bc_next_update_status",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "bc_next_update_type",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "bc_next_update_version",
                table: "oe_project_environments");
        }
    }
}
