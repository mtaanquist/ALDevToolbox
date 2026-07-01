using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<TimeOnly>(
                name: "update_window_end",
                table: "oe_project_environments",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "update_window_start",
                table: "oe_project_environments",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "scheduled_outside_window",
                table: "oe_project_deliveries",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "update_window_end",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "update_window_start",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "scheduled_outside_window",
                table: "oe_project_deliveries");
        }
    }
}
