using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordDeliveryTimings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "finished_at",
                table: "oe_project_delivery_results",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "previous_version",
                table: "oe_project_delivery_results",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "started_at",
                table: "oe_project_delivery_results",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "cancelled_by_user_id",
                table: "oe_project_deliveries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "install_started_at",
                table: "oe_project_deliveries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_deliveries_cancelled_by_user_id",
                table: "oe_project_deliveries",
                column: "cancelled_by_user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_oe_project_deliveries_users_cancelled_by_user_id",
                table: "oe_project_deliveries",
                column: "cancelled_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oe_project_deliveries_users_cancelled_by_user_id",
                table: "oe_project_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_oe_project_deliveries_cancelled_by_user_id",
                table: "oe_project_deliveries");

            migrationBuilder.DropColumn(
                name: "finished_at",
                table: "oe_project_delivery_results");

            migrationBuilder.DropColumn(
                name: "previous_version",
                table: "oe_project_delivery_results");

            migrationBuilder.DropColumn(
                name: "started_at",
                table: "oe_project_delivery_results");

            migrationBuilder.DropColumn(
                name: "cancelled_by_user_id",
                table: "oe_project_deliveries");

            migrationBuilder.DropColumn(
                name: "install_started_at",
                table: "oe_project_deliveries");
        }
    }
}
