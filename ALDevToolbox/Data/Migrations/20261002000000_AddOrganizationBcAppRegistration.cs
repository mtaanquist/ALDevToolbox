using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationBcAppRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bc_client_id",
                table: "organization_settings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bc_client_secret_encrypted",
                table: "organization_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "bc_client_secret_expires_at",
                table: "organization_settings",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bc_client_id",
                table: "organization_settings");

            migrationBuilder.DropColumn(
                name: "bc_client_secret_encrypted",
                table: "organization_settings");

            migrationBuilder.DropColumn(
                name: "bc_client_secret_expires_at",
                table: "organization_settings");
        }
    }
}
