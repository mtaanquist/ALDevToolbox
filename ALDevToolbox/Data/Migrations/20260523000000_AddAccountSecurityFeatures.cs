using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Account-security milestone: two-factor authentication (TOTP + email),
    /// passkeys (WebAuthn), pending-email change for the admin-initiated flow.
    ///
    /// Schema changes:
    /// - <c>users</c>: <c>totp_enabled</c>, <c>email_mfa_enabled</c> (both
    ///   default false), <c>pending_email</c>, <c>pending_email_at</c>.
    /// - new <c>user_totp_secrets</c>: encrypted Base32 secret + confirmation
    ///   timestamp; one row per user.
    /// - new <c>user_recovery_codes</c>: ten BCrypt-hashed single-use codes
    ///   issued at TOTP enrollment.
    /// - new <c>user_passkeys</c>: WebAuthn credentials, many per user, with
    ///   a globally-unique <c>credential_id</c>.
    ///
    /// All flags default false / null — existing users keep working without
    /// being forced into 2FA, and no data backfill is needed.
    /// </summary>
    public partial class AddAccountSecurityFeatures : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "totp_enabled",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "email_mfa_enabled",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "pending_email",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "pending_email_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "user_totp_secrets",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    secret_encrypted = table.Column<string>(type: "text", nullable: false),
                    confirmed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_totp_secrets", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_totp_secrets_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_totp_secrets_user_id",
                table: "user_totp_secrets",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateTable(
                name: "user_recovery_codes",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    code_hash = table.Column<string>(type: "text", nullable: false),
                    consumed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_recovery_codes", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_recovery_codes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_recovery_codes_user_id",
                table: "user_recovery_codes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_recovery_codes_user_id_code_hash",
                table: "user_recovery_codes",
                columns: new[] { "user_id", "code_hash" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "user_passkeys",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    credential_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    public_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    sign_counter = table.Column<long>(type: "bigint", nullable: false),
                    transports = table.Column<string>(type: "text", nullable: false),
                    aaguid = table.Column<Guid>(type: "uuid", nullable: true),
                    nickname = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_passkeys", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_passkeys_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_passkeys_credential_id",
                table: "user_passkeys",
                column: "credential_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_passkeys_user_id",
                table: "user_passkeys",
                column: "user_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("user_passkeys");
            migrationBuilder.DropTable("user_recovery_codes");
            migrationBuilder.DropTable("user_totp_secrets");
            migrationBuilder.DropColumn("pending_email_at", "users");
            migrationBuilder.DropColumn("pending_email", "users");
            migrationBuilder.DropColumn("email_mfa_enabled", "users");
            migrationBuilder.DropColumn("totp_enabled", "users");
        }
    }
}
