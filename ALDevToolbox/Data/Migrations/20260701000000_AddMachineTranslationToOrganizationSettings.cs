using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMachineTranslationToOrganizationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "machine_translation_api_key_encrypted",
                table: "organization_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "machine_translation_provider",
                table: "organization_settings",
                type: "text",
                nullable: false,
                defaultValue: "deepl");

            migrationBuilder.AddColumn<int>(
                name: "machine_translation_trigger",
                table: "organization_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "machine_translation_api_key_encrypted",
                table: "organization_settings");

            migrationBuilder.DropColumn(
                name: "machine_translation_provider",
                table: "organization_settings");

            migrationBuilder.DropColumn(
                name: "machine_translation_trigger",
                table: "organization_settings");
        }
    }
}
