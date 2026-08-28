using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExpandProjectEnvironment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "aad_tenant_id",
                table: "oe_project_environments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "app_source_apps_update_cadence",
                table: "oe_project_environments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "application_family",
                table: "oe_project_environments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "country_code",
                table: "oe_project_environments",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "delete_reason",
                table: "oe_project_environments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "enforced_update_period_start_date",
                table: "oe_project_environments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "friendly_name",
                table: "oe_project_environments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "geo_name",
                table: "oe_project_environments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "grace_period_start_date",
                table: "oe_project_environments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "hard_delete_pending_on",
                table: "oe_project_environments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "location_name",
                table: "oe_project_environments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ring_name",
                table: "oe_project_environments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "soft_deleted_on",
                table: "oe_project_environments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "oe_project_environments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "status_fetched_at",
                table: "oe_project_environments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "version",
                table: "oe_project_environments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "web_client_login_url",
                table: "oe_project_environments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "aad_tenant_id",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "app_source_apps_update_cadence",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "application_family",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "country_code",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "delete_reason",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "enforced_update_period_start_date",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "friendly_name",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "geo_name",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "grace_period_start_date",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "hard_delete_pending_on",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "location_name",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "ring_name",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "soft_deleted_on",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "status",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "status_fetched_at",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "version",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "web_client_login_url",
                table: "oe_project_environments");
        }
    }
}
