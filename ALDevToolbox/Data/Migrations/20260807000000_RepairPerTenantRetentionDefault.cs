using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Repairs two retention columns on the singleton system_settings row that
    /// every database has been carrying as 0, a value their own validation
    /// rejects.
    /// </summary>
    /// <remarks>
    /// Both columns were added by <c>AddColumn</c> with <c>defaultValue: 0</c>,
    /// which is what the already-inserted singleton row got. The entities'
    /// defaults (30 and 90) only apply to rows created through EF, and this row
    /// is created by migration - so the defaults never applied anywhere.
    ///
    /// That is not cosmetic. SaveAsync validates the whole input, and
    /// SettingsInputBuilder.Base() carries every stored field across into every
    /// tab's save. A 0 fails the 1..365 rule, so saving *any* settings tab -
    /// SMTP, banner, tools - was rejected with "Per-tenant retention must be
    /// between 1 and 365", naming a field that tab does not even show. The
    /// off-site column blocked its own tab the same way. Found by driving the
    /// settings pages during the PR 9a port; see PageSettings in
    /// .design/handoff/.
    ///
    /// Data-only on purpose: adding database-level defaults would drift from
    /// the model snapshot, which has none, and the entity defaults already
    /// cover rows EF creates.
    /// </remarks>
    public partial class RepairPerTenantRetentionDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                update system_settings
                   set per_tenant_backup_retention_count = 30
                 where per_tenant_backup_retention_count < 1
                    or per_tenant_backup_retention_count > 365;
                """);

            // 20260605000000_OffsiteBackups has exactly the same shape: entity
            // default 90, column default 0, validation demanding 1..3650. It
            // blocked saving the Off-site tab the same way.
            migrationBuilder.Sql("""
                update system_settings
                   set offsite_retention_days = 90
                 where offsite_retention_days < 1
                    or offsite_retention_days > 3650;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. Down() would have to restore a value that was
            // never valid, and there is nothing to record which rows to put it
            // back on.
        }
    }
}
