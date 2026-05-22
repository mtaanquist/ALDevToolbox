using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSignupEmailDomainAllowlistAndRequireStrongAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "signup_email_domain_allowlist",
                table: "system_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "require_strong_auth",
                table: "organization_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "signup_email_domain_allowlist",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "require_strong_auth",
                table: "organization_settings");
        }
    }
}
