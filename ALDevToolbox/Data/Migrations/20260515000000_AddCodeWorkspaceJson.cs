using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Adds <c>organization_settings.code_workspace_json</c>: the admin-editable
    /// JSON template for each org's <c>{{short_name}}.code-workspace</c> file. The
    /// generator overlays the computed <c>folders</c> array onto whatever the
    /// admin has saved (see Issue #61 and
    /// <c>.design/generation-engine.md</c>).
    ///
    /// The seed below is a verbatim copy of the previously hard-coded
    /// <c>WorkspaceSettingsJson</c> constant from
    /// <c>GenerationService.cs</c> so existing organisations keep today's
    /// behaviour byte-for-byte. The string is intentionally *not* read from the
    /// in-app <see cref="Domain.ValueObjects.OrganizationDefaults"/> constant —
    /// freezing it here ensures future edits to that constant can't
    /// retroactively change applied migrations.
    /// </summary>
    public partial class AddCodeWorkspaceJson : Migration
    {
        private const string SeedJson = """
            {
              "settings": {
                "editor.formatOnSave": true,
                "editor.autoIndent": "full",
                "editor.detectIndentation": false,
                "editor.tabSize": 4,
                "editor.insertSpaces": true,
                "al.codeAnalyzers": [
                  "${CodeCop}",
                  "${AppSourceCop}",
                  "${UICop}"
                ],
                "al.enableCodeAnalysis": true,
                "al.ruleSetPath": "../.assets/rulesets/Company.ruleset.json"
              }
            }
            """;

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add the column with the seed as a temporary default so the
            // backfill is atomic with the schema change for existing rows...
            migrationBuilder.AddColumn<string>(
                name: "code_workspace_json",
                table: "organization_settings",
                type: "text",
                nullable: false,
                defaultValue: SeedJson);

            // ...then drop the default. Future inserts must specify a value;
            // the application carries the same string in
            // OrganizationDefaults.CodeWorkspaceJson for new-row provisioning.
            // Plain SQL beats AlterColumn here — the only thing we're changing
            // is the column default, and DROP DEFAULT is unambiguous.
            migrationBuilder.Sql(
                "ALTER TABLE organization_settings ALTER COLUMN code_workspace_json DROP DEFAULT");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "code_workspace_json",
                table: "organization_settings");
        }
    }
}
