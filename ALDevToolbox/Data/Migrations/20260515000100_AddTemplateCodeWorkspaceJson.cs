using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Adds <c>runtime_templates.code_workspace_json</c>: optional per-template
    /// overlay applied on top of <c>organization_settings.code_workspace_json</c>
    /// at generation time. Nullable so existing templates inherit the org
    /// baseline unchanged — the merge in
    /// <see cref="Services.GenerationService"/> treats a null column as "no
    /// template-level additions" (issue #61).
    /// </summary>
    public partial class AddTemplateCodeWorkspaceJson : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "code_workspace_json",
                table: "runtime_templates",
                type: "text",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "code_workspace_json",
                table: "runtime_templates");
        }
    }
}
