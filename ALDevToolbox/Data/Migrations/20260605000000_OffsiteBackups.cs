using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class OffsiteBackups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "offsite_access_key_encrypted",
                table: "system_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "offsite_backup_enabled",
                table: "system_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "offsite_bucket",
                table: "system_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "offsite_endpoint",
                table: "system_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "offsite_force_path_style",
                table: "system_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "offsite_prefix",
                table: "system_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "offsite_region",
                table: "system_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "offsite_retention_days",
                table: "system_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "offsite_secret_key_encrypted",
                table: "system_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "offsite_object_key",
                table: "backups",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "offsite_uploaded_at",
                table: "backups",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "offsite_access_key_encrypted",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "offsite_backup_enabled",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "offsite_bucket",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "offsite_endpoint",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "offsite_force_path_style",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "offsite_prefix",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "offsite_region",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "offsite_retention_days",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "offsite_secret_key_encrypted",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "offsite_object_key",
                table: "backups");

            migrationBuilder.DropColumn(
                name: "offsite_uploaded_at",
                table: "backups");
        }
    }
}
