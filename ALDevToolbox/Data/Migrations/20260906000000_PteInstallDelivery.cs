using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Moves delivery onto the Admin Center's App Management API (<c>pteInstall</c>),
    /// which replaces the retired per-company <c>extensionUpload</c> path.
    /// <para>
    /// The stored mode values are sent to Business Central verbatim, and both vocabularies
    /// changed, so the rename is not enough on its own — the values have to move with the
    /// columns or every existing pipeline would post something the new API rejects.
    /// <c>company_id</c> becomes nullable rather than being dropped: extensions install
    /// per environment, so nothing writes it any more, but historical rows keep what they
    /// recorded until the automation client goes.
    /// </para>
    /// See <c>.design/saas-delivery.md</c>.
    /// </summary>
    public partial class PteInstallDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "version_mode",
                table: "oe_release_pipelines",
                newName: "deployment_schedule");

            migrationBuilder.RenameColumn(
                name: "version_mode",
                table: "oe_project_deliveries",
                newName: "deployment_schedule");

            migrationBuilder.AddColumn<Guid>(
                name: "operation_id",
                table: "oe_project_delivery_results",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "company_id",
                table: "oe_project_deliveries",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            // The value backfills. "Current Version" / "Next Minor Version" /
            // "Next Major Version" were the old upload API's wording; the new one names a
            // *time* and spells it without spaces. Anything outside the three known values
            // is left alone: it would be a value we never wrote, and rewriting it to a
            // guess is worse than the edit screen refusing it.
            foreach (var table in new[] { "oe_release_pipelines", "oe_project_deliveries" })
            {
                migrationBuilder.Sql($"""
                    UPDATE {table} SET deployment_schedule = CASE deployment_schedule
                        WHEN 'Current Version'    THEN 'Immediate'
                        WHEN 'Next Minor Version' THEN 'NextMinorUpdate'
                        WHEN 'Next Major Version' THEN 'NextMajorUpdate'
                        ELSE deployment_schedule END;
                    """);
                // Only the space moved here: 'Add' is spelled the same in both APIs.
                migrationBuilder.Sql($"""
                    UPDATE {table} SET schema_sync_mode = 'ForceSync' WHERE schema_sync_mode = 'Force Sync';
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Put the old wording back before the columns are renamed, so a rolled-back
            // database is one the old code can actually read.
            foreach (var table in new[] { "oe_release_pipelines", "oe_project_deliveries" })
            {
                migrationBuilder.Sql($"""
                    UPDATE {table} SET deployment_schedule = CASE deployment_schedule
                        WHEN 'Immediate'       THEN 'Current Version'
                        WHEN 'NextMinorUpdate' THEN 'Next Minor Version'
                        WHEN 'NextMajorUpdate' THEN 'Next Major Version'
                        ELSE deployment_schedule END;
                    """);
                migrationBuilder.Sql($"""
                    UPDATE {table} SET schema_sync_mode = 'Force Sync' WHERE schema_sync_mode = 'ForceSync';
                    """);
            }

            migrationBuilder.DropColumn(
                name: "operation_id",
                table: "oe_project_delivery_results");

            migrationBuilder.RenameColumn(
                name: "deployment_schedule",
                table: "oe_release_pipelines",
                newName: "version_mode");

            migrationBuilder.RenameColumn(
                name: "deployment_schedule",
                table: "oe_project_deliveries",
                newName: "version_mode");

            // Deliveries created after the company stopped being written have none, and
            // the column is about to be NOT NULL again.
            migrationBuilder.Sql("""
                UPDATE oe_project_deliveries SET company_id = '00000000-0000-0000-0000-000000000000' WHERE company_id IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "company_id",
                table: "oe_project_deliveries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
