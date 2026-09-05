using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class GitHubAppConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "github_app_id",
                table: "system_settings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "github_app_slug",
                table: "system_settings",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "github_client_id",
                table: "system_settings",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "github_client_secret_encrypted",
                table: "system_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "github_private_key_encrypted",
                table: "system_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "github_connected_at",
                table: "organization_settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "github_installation_id",
                table: "organization_settings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "github_installation_permissions",
                table: "organization_settings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "github_org_login",
                table: "organization_settings",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "github_app_id",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "github_app_slug",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "github_client_id",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "github_client_secret_encrypted",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "github_private_key_encrypted",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "github_connected_at",
                table: "organization_settings");

            migrationBuilder.DropColumn(
                name: "github_installation_id",
                table: "organization_settings");

            migrationBuilder.DropColumn(
                name: "github_installation_permissions",
                table: "organization_settings");

            migrationBuilder.DropColumn(
                name: "github_org_login",
                table: "organization_settings");
        }
    }
}
