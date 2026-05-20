using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Adds the <c>default_application_version_latest</c> flag to
    /// <c>runtime_templates</c>. When true, the New Workspace / New Extension
    /// forms pre-select "Latest" instead of the fixed catalogue row referenced
    /// by <c>default_application_version_id</c>; submission resolves to the
    /// highest-ordered active <c>application_versions</c> row at request time.
    /// </summary>
    public partial class AddRuntimeTemplateDefaultApplicationVersionLatest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "default_application_version_latest",
                table: "runtime_templates",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "default_application_version_latest",
                table: "runtime_templates");
        }
    }
}
