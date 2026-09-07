using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNamingSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "extension_prefix",
                table: "organization_settings",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "extension_prefix_mode",
                table: "organization_settings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "PerWorkspace");

            migrationBuilder.AddColumn<string>(
                name: "naming_folder_style",
                table: "organization_settings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "PascalCase");

            migrationBuilder.AddColumn<string>(
                name: "naming_repository_style",
                table: "organization_settings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "KebabCase");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "extension_prefix",
                table: "organization_settings");

            migrationBuilder.DropColumn(
                name: "extension_prefix_mode",
                table: "organization_settings");

            migrationBuilder.DropColumn(
                name: "naming_folder_style",
                table: "organization_settings");

            migrationBuilder.DropColumn(
                name: "naming_repository_style",
                table: "organization_settings");
        }
    }
}
