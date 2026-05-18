using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Adds the SiteAdmin runtime toggle for the MCP server. The
    /// deployment-level <c>Mcp:Enabled</c> in appsettings still controls
    /// whether <c>/mcp</c> is mapped at startup; this column is the
    /// operator-level switch surfaced on <c>/site-admin/settings</c>.
    /// Defaults to <c>false</c> — a fresh install opts in explicitly.
    /// </summary>
    public partial class AddMcpEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "mcp_enabled",
                table: "system_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "mcp_enabled",
                table: "system_settings");
        }
    }
}
