using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Retires the <c>app_source_cop_json</c> column on <c>runtime_templates</c>.
    /// AppSourceCop.json is no longer a hard-wired output of the generator —
    /// templates that want one declare it as a regular file under a folder
    /// with an empty <c>path</c> (the structured admin form renders such a
    /// row as <em>(extension root)</em>).
    ///
    /// Data is preserved in two steps before the column is dropped:
    /// <list type="number">
    ///   <item><description>The <c>mandatoryPrefix</c> value (used by the
    ///   <c>{{prefix}}</c> mustache variable) is moved into
    ///   <c>defaults_json.affix</c>, and <c>affixType</c> is stamped
    ///   <c>"Prefix"</c> to preserve current substitution semantics.</description></item>
    ///   <item><description>For every existing template a new
    ///   <c>template_folders</c> row with <c>path = ''</c> is inserted, and
    ///   the column's previous content is dropped into a child
    ///   <c>template_files</c> row named <c>AppSourceCop.json</c>. Module
    ///   extensions get the same treatment via <c>template_module_folders</c>
    ///   / <c>template_module_files</c> so the generator's previous
    ///   per-module AppSourceCop.json emission is preserved unchanged.</description></item>
    /// </list>
    /// Admins who don't want an AppSourceCop.json (e.g. per-tenant
    /// extensions) can delete the new row from the structured admin form;
    /// no value is lost.
    /// </summary>
    public partial class DropAppSourceCopColumn : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Move mandatoryPrefix into defaults_json as affix (string) and
            // stamp affixType as "Prefix". jsonb_set creates intermediate
            // nodes as needed; the COALESCE handles templates with empty
            // app_source_cop_json content.
            migrationBuilder.Sql(@"
                UPDATE runtime_templates
                SET defaults_json = jsonb_set(
                    jsonb_set(
                        defaults_json,
                        '{affix}',
                        COALESCE(app_source_cop_json->'mandatoryPrefix', '""""'::jsonb)
                    ),
                    '{affixType}',
                    '""Prefix""'::jsonb
                );
            ");

            // Materialise each template's AppSourceCop.json as a root file
            // under the Core extension. Ordering = -1 puts the root row
            // first when ordered ascending; existing rows keep their 0..n
            // positions. The CTE feeds the new folder id into the matching
            // file insert in one statement.
            migrationBuilder.Sql(@"
                WITH new_folders AS (
                    INSERT INTO template_folders (organization_id, template_id, ordering, path)
                    SELECT organization_id, id, -1, ''
                    FROM runtime_templates
                    RETURNING id AS folder_id, template_id
                )
                INSERT INTO template_files
                    (organization_id, template_folder_id, ordering, path, content, is_example)
                SELECT rt.organization_id, nf.folder_id, 0, 'AppSourceCop.json',
                       COALESCE(rt.app_source_cop_json::text, '{}'),
                       false
                FROM new_folders nf
                JOIN runtime_templates rt ON rt.id = nf.template_id;
            ");

            // Same for module extensions so the generator's pre-refactor
            // per-module AppSourceCop.json emission is preserved.
            migrationBuilder.Sql(@"
                WITH new_module_folders AS (
                    INSERT INTO template_module_folders (organization_id, template_id, ordering, path)
                    SELECT organization_id, id, -1, ''
                    FROM runtime_templates
                    RETURNING id AS folder_id, template_id
                )
                INSERT INTO template_module_files
                    (organization_id, template_module_folder_id, ordering, path, content, is_example)
                SELECT rt.organization_id, nmf.folder_id, 0, 'AppSourceCop.json',
                       COALESCE(rt.app_source_cop_json::text, '{}'),
                       false
                FROM new_module_folders nmf
                JOIN runtime_templates rt ON rt.id = nmf.template_id;
            ");

            migrationBuilder.DropColumn(name: "app_source_cop_json", table: "runtime_templates");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "app_source_cop_json",
                table: "runtime_templates",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            // Reverse direction: pull the root-folder AppSourceCop.json
            // content back into the column. Pick the first matching row per
            // template to avoid duplicates if an admin added more than one
            // root-level AppSourceCop.json (unlikely; the unique index on
            // (template_folder_id, path) prevents duplicates within a
            // single folder).
            migrationBuilder.Sql(@"
                UPDATE runtime_templates rt
                SET app_source_cop_json = COALESCE((
                    SELECT tf.content::jsonb
                    FROM template_files tf
                    JOIN template_folders tfd ON tfd.id = tf.template_folder_id
                    WHERE tfd.template_id = rt.id
                      AND tfd.path = ''
                      AND tf.path = 'AppSourceCop.json'
                    LIMIT 1
                ), '{}'::jsonb);
            ");

            // Drop the root rows we created on the way up. Only delete
            // empty-path folders whose only file is AppSourceCop.json —
            // anything else means an admin added their own root file and
            // we should leave it alone.
            migrationBuilder.Sql(@"
                DELETE FROM template_module_folders
                WHERE path = ''
                  AND id IN (
                      SELECT folder_id FROM (
                          SELECT tmf.template_module_folder_id AS folder_id,
                                 COUNT(*) FILTER (WHERE tmf.path = 'AppSourceCop.json') AS appsourcecop_count,
                                 COUNT(*) AS total
                          FROM template_module_files tmf
                          GROUP BY tmf.template_module_folder_id
                      ) c
                      WHERE c.total = c.appsourcecop_count
                  );
            ");
            migrationBuilder.Sql(@"
                DELETE FROM template_folders
                WHERE path = ''
                  AND id IN (
                      SELECT folder_id FROM (
                          SELECT tf.template_folder_id AS folder_id,
                                 COUNT(*) FILTER (WHERE tf.path = 'AppSourceCop.json') AS appsourcecop_count,
                                 COUNT(*) AS total
                          FROM template_files tf
                          GROUP BY tf.template_folder_id
                      ) c
                      WHERE c.total = c.appsourcecop_count
                  );
            ");

            // affix / affixType in defaults_json are left in place on the
            // way down — there's no clean way to know whether an admin
            // edited them since the up-migration, and the json shape is
            // forward-compatible.
        }
    }
}
