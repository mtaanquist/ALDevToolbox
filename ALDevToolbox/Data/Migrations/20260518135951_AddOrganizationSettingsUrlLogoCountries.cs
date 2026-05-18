using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationSettingsUrlLogoCountries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "default_logo",
                table: "organization_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "default_supported_countries",
                table: "organization_settings",
                type: "text[]",
                nullable: false,
                defaultValueSql: "ARRAY[]::text[]");

            migrationBuilder.AddColumn<string>(
                name: "default_url",
                table: "organization_settings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "default_logo",
                table: "organization_settings");

            migrationBuilder.DropColumn(
                name: "default_supported_countries",
                table: "organization_settings");

            migrationBuilder.DropColumn(
                name: "default_url",
                table: "organization_settings");
        }
    }
}
