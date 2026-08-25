using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEntraSecretExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "entra_client_secret_expires_at",
                table: "system_settings",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "entra_client_secret_expires_at",
                table: "organization_settings",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "entra_client_secret_expires_at",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "entra_client_secret_expires_at",
                table: "organization_settings");
        }
    }
}
