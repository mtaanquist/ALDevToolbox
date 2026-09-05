using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class GitHubUserLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "access_token_encrypted",
                table: "user_external_logins",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "access_token_expires_at",
                table: "user_external_logins",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_org_member",
                table: "user_external_logins",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "refresh_token_encrypted",
                table: "user_external_logins",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "access_token_encrypted",
                table: "user_external_logins");

            migrationBuilder.DropColumn(
                name: "access_token_expires_at",
                table: "user_external_logins");

            migrationBuilder.DropColumn(
                name: "is_org_member",
                table: "user_external_logins");

            migrationBuilder.DropColumn(
                name: "refresh_token_encrypted",
                table: "user_external_logins");
        }
    }
}
