using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Milestone P4.19 — invite-by-email and magic-link login.
    ///
    /// Adds the <c>invites</c> table (admin-issued single-use tokens with a
    /// 7-day expiry) and a <c>purpose</c> column on
    /// <c>password_reset_tokens</c> so the same table can carry magic-link
    /// tokens alongside password resets — different lifetime, different
    /// consumer, identical storage contract. Existing rows are stamped with
    /// <c>'PasswordReset'</c> via the column default.
    /// </summary>
    public partial class InvitesAndMagicLink : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "purpose",
                table: "password_reset_tokens",
                type: "text",
                nullable: false,
                defaultValue: "PasswordReset");

            migrationBuilder.CreateTable(
                name: "invites",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    welcome_message = table.Column<string>(type: "text", nullable: true),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    invited_by_user_id = table.Column<int>(type: "integer", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invites", x => x.id);
                    table.ForeignKey(
                        name: "FK_invites_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_invites_users_invited_by_user_id",
                        column: x => x.invited_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_invites_token_hash",
                table: "invites",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invites_organization_id_email",
                table: "invites",
                columns: new[] { "organization_id", "email" });

            migrationBuilder.CreateIndex(
                name: "IX_invites_invited_by_user_id",
                table: "invites",
                column: "invited_by_user_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("invites");
            migrationBuilder.DropColumn(name: "purpose", table: "password_reset_tokens");
        }
    }
}
