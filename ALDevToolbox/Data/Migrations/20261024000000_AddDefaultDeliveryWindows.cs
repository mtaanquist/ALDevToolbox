using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDefaultDeliveryWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<TimeOnly>(
                name: "default_delivery_window_production_end",
                table: "organization_settings",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "default_delivery_window_production_start",
                table: "organization_settings",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "default_delivery_window_sandbox_end",
                table: "organization_settings",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "default_delivery_window_sandbox_start",
                table: "organization_settings",
                type: "time without time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "default_delivery_window_production_end",
                table: "organization_settings");

            migrationBuilder.DropColumn(
                name: "default_delivery_window_production_start",
                table: "organization_settings");

            migrationBuilder.DropColumn(
                name: "default_delivery_window_sandbox_end",
                table: "organization_settings");

            migrationBuilder.DropColumn(
                name: "default_delivery_window_sandbox_start",
                table: "organization_settings");
        }
    }
}
