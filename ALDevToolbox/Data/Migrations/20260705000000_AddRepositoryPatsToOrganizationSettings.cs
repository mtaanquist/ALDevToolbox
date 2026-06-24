using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositoryPatsToOrganizationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "azure_devops_pat_encrypted",
                table: "organization_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "github_pat_encrypted",
                table: "organization_settings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "azure_devops_pat_encrypted",
                table: "organization_settings");

            migrationBuilder.DropColumn(
                name: "github_pat_encrypted",
                table: "organization_settings");
        }
    }
}
