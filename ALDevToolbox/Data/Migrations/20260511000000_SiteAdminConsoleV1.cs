using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    public partial class SiteAdminConsoleV1 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_site_admin",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "system_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    smtp_host = table.Column<string>(type: "text", nullable: true),
                    smtp_port = table.Column<int>(type: "integer", nullable: true),
                    smtp_user = table.Column<string>(type: "text", nullable: true),
                    smtp_password_encrypted = table.Column<string>(type: "text", nullable: true),
                    smtp_from = table.Column<string>(type: "text", nullable: true),
                    smtp_use_starttls = table.Column<bool>(type: "boolean", nullable: true),
                    banner_text = table.Column<string>(type: "text", nullable: true),
                    default_signup_auto_approve = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table => table.PrimaryKey("PK_system_settings", x => x.id));

            // Singleton row pinned to id = 1 so SystemSettingsService can read
            // it unconditionally without first deciding whether to insert.
            migrationBuilder.Sql(@"
                INSERT INTO system_settings (
                    id, default_signup_auto_approve, updated_at
                ) VALUES (
                    1, false, '2026-05-11T00:00:00Z' AT TIME ZONE 'UTC'
                );
            ");

            // Upgrade compatibility: existing deployments don't have a
            // SiteAdmin yet (the column was just added with default false),
            // so the SiteAdmin console would be unreachable until someone
            // hand-edits the database. Promote the first active admin in
            // the Default organisation to SiteAdmin so the operator can
            // sign in and grant the flag to additional users from the UI.
            // No-op on fresh databases (no users yet); the bootstrap path
            // in Program.cs handles those.
            migrationBuilder.Sql(@"
                UPDATE users
                   SET is_site_admin = true
                 WHERE id = (
                    SELECT u.id
                      FROM users u
                      JOIN organizations o ON o.id = u.organization_id
                     WHERE o.slug = 'default'
                       AND u.role = 'Admin'
                       AND u.status = 'Active'
                     ORDER BY u.created_at ASC
                     LIMIT 1
                 );
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("system_settings");
            migrationBuilder.DropColumn(name: "is_site_admin", table: "users");
        }
    }
}
