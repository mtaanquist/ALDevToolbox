using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildProvenanceAndRelaxCustomerLabel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_oe_releases_org_label_active",
                table: "oe_releases");

            migrationBuilder.AddColumn<DateTime>(
                name: "commit_date",
                table: "oe_customer_build_results",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "commit_sha",
                table: "oe_customer_build_results",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "repo_url",
                table: "oe_customer_build_results",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_oe_releases_org_label_active",
                table: "oe_releases",
                columns: new[] { "organization_id", "label" },
                unique: true,
                filter: "\"deleted_at\" IS NULL AND \"kind\" <> 'customer'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_oe_releases_org_label_active",
                table: "oe_releases");

            migrationBuilder.DropColumn(
                name: "commit_date",
                table: "oe_customer_build_results");

            migrationBuilder.DropColumn(
                name: "commit_sha",
                table: "oe_customer_build_results");

            migrationBuilder.DropColumn(
                name: "repo_url",
                table: "oe_customer_build_results");

            migrationBuilder.CreateIndex(
                name: "ix_oe_releases_org_label_active",
                table: "oe_releases",
                columns: new[] { "organization_id", "label" },
                unique: true,
                filter: "\"deleted_at\" IS NULL");
        }
    }
}
