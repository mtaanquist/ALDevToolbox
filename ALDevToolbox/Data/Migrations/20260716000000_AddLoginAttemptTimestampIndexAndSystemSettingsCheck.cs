using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLoginAttemptTimestampIndexAndSystemSettingsCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_system_settings_singleton",
                table: "system_settings",
                sql: "id = 1");

            migrationBuilder.CreateIndex(
                name: "IX_login_attempts_timestamp",
                table: "login_attempts",
                column: "timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_system_settings_singleton",
                table: "system_settings");

            migrationBuilder.DropIndex(
                name: "IX_login_attempts_timestamp",
                table: "login_attempts");
        }
    }
}
