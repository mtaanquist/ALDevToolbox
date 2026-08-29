using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Mirrors <em>Microsoft's</em> platform-update window onto each environment, as
    /// read-only context beside the toolbox's own delivery window.
    /// <para>
    /// The two are different things and stay in different columns:
    /// <c>update_window_start</c>/<c>_end</c> is the delivery slot agreed with the
    /// customer and enforced by our worker; the <c>bc_update_window_*</c> columns added
    /// here are when Microsoft patches the environment. Neither is derived from the
    /// other — the only relationship worth drawing is whether they overlap, which the
    /// project page warns about.
    /// </para>
    /// <para>
    /// The zone is stored twice on purpose: Business Central speaks Windows time-zone
    /// ids and only accepts one back on a write, while display maths on Linux needs the
    /// IANA form. All columns are nullable — an environment may have no window, and a
    /// row that predates this has not been read yet.
    /// </para>
    /// See <c>.design/saas-delivery.md</c>.
    /// </summary>
    public partial class MirrorBcUpdateWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<TimeOnly>(
                name: "bc_update_window_end",
                table: "oe_project_environments",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "bc_update_window_fetched_at",
                table: "oe_project_environments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "bc_update_window_start",
                table: "oe_project_environments",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bc_update_window_time_zone_iana",
                table: "oe_project_environments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bc_update_window_time_zone_id",
                table: "oe_project_environments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bc_update_window_end",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "bc_update_window_fetched_at",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "bc_update_window_start",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "bc_update_window_time_zone_iana",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "bc_update_window_time_zone_id",
                table: "oe_project_environments");
        }
    }
}
