using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailVerifiedSignup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "auto_join_verified_domain_users",
                table: "organization_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "pending_signups",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    email = table.Column<string>(type: "text", nullable: false),
                    link_token_hash = table.Column<string>(type: "text", nullable: false),
                    code_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_signups", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pending_signups_email",
                table: "pending_signups",
                column: "email");

            migrationBuilder.CreateIndex(
                name: "IX_pending_signups_link_token_hash",
                table: "pending_signups",
                column: "link_token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_pending_signups_email_active",
                table: "pending_signups",
                column: "email",
                unique: true,
                filter: "verified_at IS NULL AND completed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_signups");

            migrationBuilder.DropColumn(
                name: "auto_join_verified_domain_users",
                table: "organization_settings");
        }
    }
}
