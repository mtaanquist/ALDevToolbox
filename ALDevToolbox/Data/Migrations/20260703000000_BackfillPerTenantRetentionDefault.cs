using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackfillPerTenantRetentionDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The singleton system_settings row (id = 1) is seeded by
            // 20260511000000_SiteAdminConsoleV1, before
            // per_tenant_backup_retention_count existed. 20260604000000_PerTenantBackups
            // later added that column with a DB default of 0 — so on every fresh
            // install the seeded row carries 0, which SaveAsync rejects (valid range
            // 1..365) and which blocks saving *any* settings section. Heal any row
            // still holding the 0 default to the entity default (30). 0 is never a
            // valid saved value, so this only touches the bad migration default.
            migrationBuilder.Sql(
                "UPDATE system_settings SET per_tenant_backup_retention_count = 30 WHERE per_tenant_backup_retention_count = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data heal only — nothing to revert.
        }
    }
}
