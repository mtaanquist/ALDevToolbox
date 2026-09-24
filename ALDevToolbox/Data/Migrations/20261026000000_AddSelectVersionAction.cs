using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSelectVersionAction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "bc_offered_versions",
                table: "oe_project_environments",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target_version",
                table: "oe_environment_upgrade_actions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bc_offered_versions",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "target_version",
                table: "oe_environment_upgrade_actions");
        }
    }
}
