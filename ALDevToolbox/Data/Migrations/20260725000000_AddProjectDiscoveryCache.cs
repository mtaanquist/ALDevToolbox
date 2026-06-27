using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectDiscoveryCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "discovered_at",
                table: "oe_projects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "discovered_extensions_json",
                table: "oe_projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "discovery_error",
                table: "oe_projects",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "discovered_at",
                table: "oe_projects");

            migrationBuilder.DropColumn(
                name: "discovered_extensions_json",
                table: "oe_projects");

            migrationBuilder.DropColumn(
                name: "discovery_error",
                table: "oe_projects");
        }
    }
}
