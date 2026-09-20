using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSolutionCustomerBasics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bc_version",
                table: "oe_projects",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "client_url",
                table: "oe_projects",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "hosting_type",
                table: "oe_projects",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "license_type",
                table: "oe_projects",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "user_experience",
                table: "oe_projects",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "voice_account_number",
                table: "oe_projects",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bc_version",
                table: "oe_projects");

            migrationBuilder.DropColumn(
                name: "client_url",
                table: "oe_projects");

            migrationBuilder.DropColumn(
                name: "hosting_type",
                table: "oe_projects");

            migrationBuilder.DropColumn(
                name: "license_type",
                table: "oe_projects");

            migrationBuilder.DropColumn(
                name: "user_experience",
                table: "oe_projects");

            migrationBuilder.DropColumn(
                name: "voice_account_number",
                table: "oe_projects");
        }
    }
}
