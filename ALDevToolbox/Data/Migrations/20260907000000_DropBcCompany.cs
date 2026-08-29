using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Removes the company from the delivery model, along with the last trace of the
    /// automation API.
    /// <para>
    /// Extensions install per <em>environment</em> and are then available to every
    /// company in it — the company was only ever an artifact of the automation API being
    /// an OData surface bound to <c>companies({id})</c>, and the App Management API that
    /// replaced it has no company segment at all. Nothing has written these columns since
    /// publishing moved, so this is a pure drop with no data to transform;
    /// <c>extension_upload_id</c> goes with the surface that issued those ids.
    /// </para>
    /// <c>Down</c> recreates all four nullable, which is the shape they had by the end —
    /// it restores the columns, not the values, and nothing reads them any more.
    /// See <c>.design/saas-delivery.md</c>.
    /// </summary>
    public partial class DropBcCompany : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "company_id",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "company_name",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "extension_upload_id",
                table: "oe_project_delivery_results");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "oe_project_deliveries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "oe_project_environments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "company_name",
                table: "oe_project_environments",
                type: "character varying(250)",
                maxLength: 250,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "extension_upload_id",
                table: "oe_project_delivery_results",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "oe_project_deliveries",
                type: "uuid",
                nullable: true);
        }
    }
}
