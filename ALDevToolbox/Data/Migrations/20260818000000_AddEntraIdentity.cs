using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEntraIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "entra_client_id",
                table: "system_settings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "entra_client_secret_encrypted",
                table: "system_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "entra_allowed_tenant_ids",
                table: "organization_settings",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string>(
                name: "entra_client_id",
                table: "organization_settings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "entra_client_secret_encrypted",
                table: "organization_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "entra_enabled",
                table: "organization_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "local_login_policy",
                table: "organization_settings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "AllowAll");

            migrationBuilder.CreateTable(
                name: "user_external_logins",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    issuer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    subject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    display_identity = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_login_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_external_logins", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_external_logins_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_external_logins_provider_issuer_subject",
                table: "user_external_logins",
                columns: new[] { "provider", "issuer", "subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_external_logins_user_id",
                table: "user_external_logins",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_external_logins");

            migrationBuilder.DropColumn(
                name: "entra_client_id",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "entra_client_secret_encrypted",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "entra_allowed_tenant_ids",
                table: "organization_settings");

            migrationBuilder.DropColumn(
                name: "entra_client_id",
                table: "organization_settings");

            migrationBuilder.DropColumn(
                name: "entra_client_secret_encrypted",
                table: "organization_settings");

            migrationBuilder.DropColumn(
                name: "entra_enabled",
                table: "organization_settings");

            migrationBuilder.DropColumn(
                name: "local_login_policy",
                table: "organization_settings");
        }
    }
}
