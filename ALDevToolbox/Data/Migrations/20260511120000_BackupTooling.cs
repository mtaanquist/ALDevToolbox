using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Milestone P4.18 — backup tooling and CI migration testing.
    ///
    /// Adds the <c>backups</c> table (one row per <c>pg_dump</c> file under the
    /// backups directory) and three new columns on <c>system_settings</c> that
    /// drive <c>BackupScheduler</c>: enabled flag, time-of-day, and retention
    /// count. Existing deployments keep working — the new columns are
    /// populated with the same defaults as a fresh row.
    /// </summary>
    public partial class BackupTooling : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "backup_schedule_enabled",
                table: "system_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "backup_schedule_time_utc",
                table: "system_settings",
                type: "time without time zone",
                nullable: false,
                defaultValueSql: "'02:00:00'");

            migrationBuilder.AddColumn<int>(
                name: "backup_retention_count",
                table: "system_settings",
                type: "integer",
                nullable: false,
                defaultValue: 14);

            migrationBuilder.CreateTable(
                name: "backups",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    file_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    is_pinned = table.Column<bool>(type: "boolean", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backups", x => x.id);
                    table.ForeignKey(
                        name: "FK_backups_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_backups_file_name",
                table: "backups",
                column: "file_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backups_created_at",
                table: "backups",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_backups_created_by_user_id",
                table: "backups",
                column: "created_by_user_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("backups");
            migrationBuilder.DropColumn(name: "backup_retention_count", table: "system_settings");
            migrationBuilder.DropColumn(name: "backup_schedule_time_utc", table: "system_settings");
            migrationBuilder.DropColumn(name: "backup_schedule_enabled", table: "system_settings");
        }
    }
}
