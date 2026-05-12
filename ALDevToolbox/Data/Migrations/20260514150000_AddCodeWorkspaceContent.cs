using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Two related changes that bring the generator under a strict
    /// "emit only what the template declares" rule:
    ///
    /// <list type="number">
    ///   <item><description>Adds a <c>code_workspace_content</c> column to
    ///   <c>runtime_templates</c>. The column carries the verbatim
    ///   <c>.code-workspace</c> JSON that the generator used to build in
    ///   code, with <c>{{paths}}</c> as the only generator-supplied
    ///   substitution. Existing rows are stamped with the previous
    ///   generator output so .code-workspace files keep their AL analyzer
    ///   list / ruleset path until an admin edits them.</description></item>
    ///   <item><description>For every existing template, inserts
    ///   <c>libs</c>, <c>permissionsets</c> and <c>Translations</c> rows
    ///   into <c>template_folders</c> and <c>template_module_folders</c>
    ///   so the generator can stop emitting those folders as static
    ///   fallbacks. New templates pick up the same rows via
    ///   <c>BlankToml()</c> / <c>FormState.Blank()</c>.</description></item>
    /// </list>
    /// </summary>
    public partial class AddCodeWorkspaceContent : Migration
    {
        /// <summary>
        /// Kept in sync with <c>GenerationService.DefaultCodeWorkspaceContent</c>
        /// — the migration can't reference application code at runtime, so the
        /// canonical default is duplicated here and asserted equal in tests.
        /// </summary>
        private const string DefaultCodeWorkspaceContent = """
{
  "folders": [
{{paths}}
  ],
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
            // Column added with the default value so existing rows pick it up
            // without a separate UPDATE; the application code keeps a plain
            // string property and writes its own default for new rows in
            // BlankToml / FormState.Blank.
            migrationBuilder.AddColumn<string>(
                name: "code_workspace_content",
                table: "runtime_templates",
                type: "text",
                nullable: false,
                defaultValue: DefaultCodeWorkspaceContent);

            // Seed the three previously-static fallback folders into every
            // existing template's Core and Module folder lists. ordering
            // values keep growing past the current max so existing rows
            // don't shuffle; the structured admin form orders by
            // (organization_id, template_id, ordering) and lets the admin
            // reorder freely after the fact.
            //
            // Skip rows where the folder already exists (case-insensitive)
            // so re-running this migration is idempotent and templates that
            // already declare these folders don't end up with duplicates.
            migrationBuilder.Sql(@"
                INSERT INTO template_folders (organization_id, template_id, ordering, path)
                SELECT rt.organization_id, rt.id,
                       COALESCE((SELECT MAX(ordering) FROM template_folders WHERE template_id = rt.id), -1) + 1,
                       'libs'
                FROM runtime_templates rt
                WHERE NOT EXISTS (
                    SELECT 1 FROM template_folders tf
                    WHERE tf.template_id = rt.id AND LOWER(tf.path) = 'libs'
                );
            ");

            migrationBuilder.Sql(@"
                INSERT INTO template_folders (organization_id, template_id, ordering, path)
                SELECT rt.organization_id, rt.id,
                       COALESCE((SELECT MAX(ordering) FROM template_folders WHERE template_id = rt.id), -1) + 1,
                       'permissionsets'
                FROM runtime_templates rt
                WHERE NOT EXISTS (
                    SELECT 1 FROM template_folders tf
                    WHERE tf.template_id = rt.id AND LOWER(tf.path) = 'permissionsets'
                );
            ");

            migrationBuilder.Sql(@"
                INSERT INTO template_folders (organization_id, template_id, ordering, path)
                SELECT rt.organization_id, rt.id,
                       COALESCE((SELECT MAX(ordering) FROM template_folders WHERE template_id = rt.id), -1) + 1,
                       'Translations'
                FROM runtime_templates rt
                WHERE NOT EXISTS (
                    SELECT 1 FROM template_folders tf
                    WHERE tf.template_id = rt.id AND LOWER(tf.path) = 'translations'
                );
            ");

            migrationBuilder.Sql(@"
                INSERT INTO template_module_folders (organization_id, template_id, ordering, path)
                SELECT rt.organization_id, rt.id,
                       COALESCE((SELECT MAX(ordering) FROM template_module_folders WHERE template_id = rt.id), -1) + 1,
                       'libs'
                FROM runtime_templates rt
                WHERE NOT EXISTS (
                    SELECT 1 FROM template_module_folders tmf
                    WHERE tmf.template_id = rt.id AND LOWER(tmf.path) = 'libs'
                );
            ");

            migrationBuilder.Sql(@"
                INSERT INTO template_module_folders (organization_id, template_id, ordering, path)
                SELECT rt.organization_id, rt.id,
                       COALESCE((SELECT MAX(ordering) FROM template_module_folders WHERE template_id = rt.id), -1) + 1,
                       'permissionsets'
                FROM runtime_templates rt
                WHERE NOT EXISTS (
                    SELECT 1 FROM template_module_folders tmf
                    WHERE tmf.template_id = rt.id AND LOWER(tmf.path) = 'permissionsets'
                );
            ");

            migrationBuilder.Sql(@"
                INSERT INTO template_module_folders (organization_id, template_id, ordering, path)
                SELECT rt.organization_id, rt.id,
                       COALESCE((SELECT MAX(ordering) FROM template_module_folders WHERE template_id = rt.id), -1) + 1,
                       'Translations'
                FROM runtime_templates rt
                WHERE NOT EXISTS (
                    SELECT 1 FROM template_module_folders tmf
                    WHERE tmf.template_id = rt.id AND LOWER(tmf.path) = 'translations'
                );
            ");

            // Drop the default now that existing rows have been stamped — new
            // rows insert an explicit value from the application code, so a
            // server-side default would just go stale if the constant moved.
            migrationBuilder.AlterColumn<string>(
                name: "code_workspace_content",
                table: "runtime_templates",
                type: "text",
                nullable: false,
                oldDefaultValue: DefaultCodeWorkspaceContent);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "code_workspace_content", table: "runtime_templates");

            // The fallback rows aren't reversible without losing data — admins
            // may have added files to them after the migration ran. Leave them
            // in place on the way down; the old generator code would have
            // collided with them but the down migration is a development aid
            // only (CI doesn't replay it).
        }
    }
}
