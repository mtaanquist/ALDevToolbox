using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Data migration: <c>{{short_name}}</c> stopped meaning "the workspace
    /// name with its whitespace stripped" and started meaning the customer's
    /// abbreviated name, which is a display value and not a path. The job the
    /// old variable was doing belongs to <c>{{workspace_folder}}</c> now, so
    /// the three places an organisation could only ever have meant a path get
    /// rewritten: both layers of the <c>.code-workspace</c> JSON, whose folder
    /// entries and file name are paths - the organisation's base template and
    /// the per-template overlay merged on top of it - and the path of an
    /// organisation file.
    ///
    /// <para>Prose uses of the variable inside a file's <em>content</em>
    /// ("Customizations made for {{short_name}}") are deliberately left alone -
    /// both readings of those are what the author meant. See
    /// <c>.design/customer-naming.md</c>.</para>
    /// </summary>
    public partial class RewriteShortNamePaths : Migration
    {
        /// <summary>
        /// The rewrite itself, exposed so the migration test can replay it
        /// against a seeded database rather than restating the SQL. Idempotent:
        /// a second run finds nothing left to replace. Both spellings are
        /// covered because the renderer still resolves the legacy camelCase
        /// alias.
        /// </summary>
        public const string RewriteSql = """
            UPDATE organization_settings
               SET code_workspace_json = REPLACE(
                       REPLACE(code_workspace_json, '{{short_name}}', '{{workspace_folder}}'),
                       '{{shortName}}', '{{workspace_folder}}')
             WHERE code_workspace_json LIKE '%{{short_name}}%'
                OR code_workspace_json LIKE '%{{shortName}}%';

            UPDATE runtime_templates
               SET code_workspace_json = REPLACE(
                       REPLACE(code_workspace_json, '{{short_name}}', '{{workspace_folder}}'),
                       '{{shortName}}', '{{workspace_folder}}')
             WHERE code_workspace_json LIKE '%{{short_name}}%'
                OR code_workspace_json LIKE '%{{shortName}}%';

            UPDATE organization_files
               SET path = REPLACE(
                       REPLACE(path, '{{short_name}}', '{{workspace_folder}}'),
                       '{{shortName}}', '{{workspace_folder}}')
             WHERE path LIKE '%{{short_name}}%'
                OR path LIKE '%{{shortName}}%';
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RewriteSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. Rewriting {{workspace_folder}} back would hit
            // every use of the new variable, including the ones that were
            // authored as {{workspace_folder}} in the first place, so undoing
            // this would lose more than it restored.
        }
    }
}
