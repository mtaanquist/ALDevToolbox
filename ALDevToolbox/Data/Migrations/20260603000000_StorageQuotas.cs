using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Per-organisation storage quotas. Adds the system-wide default and
    /// index-size multiplier on <c>system_settings</c>, plus the per-org
    /// override column on <c>organizations</c>. Quota enforcement lives in
    /// <c>StorageQuotaGuard</c>; this migration only carries the columns.
    /// </summary>
    public partial class StorageQuotas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "default_storage_quota_mb",
                table: "system_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "index_size_multiplier",
                table: "system_settings",
                type: "numeric(6,3)",
                nullable: false,
                defaultValue: 0.5m);

            migrationBuilder.AddColumn<int>(
                name: "storage_quota_mb",
                table: "organizations",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "default_storage_quota_mb",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "index_size_multiplier",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "storage_quota_mb",
                table: "organizations");
        }
    }
}
