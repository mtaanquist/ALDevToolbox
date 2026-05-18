using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationFileScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Schema first: add the scope column. Existing rows default to
            // workspace_root which keeps current behaviour for the platform
            // files (.gitignore, ruleset, README) and any admin-authored
            // rows from before this migration.
            migrationBuilder.AddColumn<string>(
                name: "scope",
                table: "organization_files",
                type: "text",
                nullable: false,
                defaultValue: "WorkspaceRoot");

            // Phase out the template-level AppSourceCop column. For every
            // template that opted into AppSourceCop.json emission today
            // (`app_source_cop_json -> include` = true):
            //   1. Ensure an `AppSourceCop.json` OrganizationFile exists
            //      for the org with scope = EveryExtension. The content is
            //      built from the template's MandatoryPrefix and the org's
            //      DefaultSupportedCountries — same shape the generator
            //      used to assemble inline.
            //   2. Join it to the template via runtime_template_included_files.
            // Templates with `include = false` are silently skipped — opting
            // them in post-migration is a click in the editor.
            //
            // We pick the first opted-in template's MandatoryPrefix to seed
            // the per-org file. If different templates declared different
            // prefixes the admin gets the first one and can split into
            // multiple OrganizationFile rows after the migration.
            migrationBuilder.Sql(@"
                INSERT INTO organization_files
                    (organization_id, path, content, mustache_enabled, scope, ordering, updated_at)
                SELECT
                    s.organization_id,
                    'AppSourceCop.json',
                    '{' || E'\n' ||
                        '    ""mandatoryPrefix"": ""' || s.mandatory_prefix || '""' ||
                        (CASE WHEN s.supported_countries IS NULL OR array_length(s.supported_countries, 1) IS NULL
                              THEN ''
                              ELSE ',' || E'\n' || '    ""supportedCountries"": [' ||
                                   (SELECT string_agg('""' || c || '""', ', ') FROM unnest(s.supported_countries) AS c) ||
                                   ']'
                         END) || E'\n' ||
                    '}' || E'\n',
                    false,
                    'EveryExtension',
                    2000,
                    now()
                FROM (
                    SELECT
                        t.organization_id,
                        COALESCE(
                            NULLIF(t.app_source_cop_json ->> 'mandatoryPrefix', ''),
                            ''
                        ) AS mandatory_prefix,
                        os.default_supported_countries AS supported_countries,
                        ROW_NUMBER() OVER (PARTITION BY t.organization_id ORDER BY t.id) AS rn
                    FROM runtime_templates t
                    LEFT JOIN organization_settings os ON os.organization_id = t.organization_id
                    WHERE t.deleted_at IS NULL
                      AND (t.app_source_cop_json ->> 'include')::boolean = true
                ) AS s
                WHERE s.rn = 1
                  AND NOT EXISTS (
                      SELECT 1 FROM organization_files f
                      WHERE f.organization_id = s.organization_id
                        AND f.path = 'AppSourceCop.json'
                  );
            ");

            // Join the freshly-created (or pre-existing) AppSourceCop.json
            // row to every template in that org that opted in. Idempotent.
            migrationBuilder.Sql(@"
                INSERT INTO runtime_template_included_files
                    (organization_id, runtime_template_id, organization_file_id, ordering)
                SELECT
                    t.organization_id,
                    t.id,
                    f.id,
                    2000
                FROM runtime_templates t
                JOIN organization_files f
                    ON f.organization_id = t.organization_id
                   AND f.path = 'AppSourceCop.json'
                WHERE t.deleted_at IS NULL
                  AND (t.app_source_cop_json ->> 'include')::boolean = true
                  AND NOT EXISTS (
                      SELECT 1 FROM runtime_template_included_files j
                      WHERE j.runtime_template_id = t.id
                        AND j.organization_file_id = f.id
                  );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The data backfill is not reversible — the source AppSourceCop
            // configuration still sits on the runtime_templates row (we
            // haven't dropped that column yet), so re-running the down path
            // just removes the freshly-created OrganizationFile rows and the
            // joins to them, leaving the structured column intact.
            migrationBuilder.Sql(@"
                DELETE FROM runtime_template_included_files
                WHERE organization_file_id IN (
                    SELECT id FROM organization_files
                    WHERE path = 'AppSourceCop.json' AND scope = 'EveryExtension'
                );
            ");
            migrationBuilder.Sql(@"
                DELETE FROM organization_files
                WHERE path = 'AppSourceCop.json' AND scope = 'EveryExtension';
            ");

            migrationBuilder.DropColumn(
                name: "scope",
                table: "organization_files");
        }
    }
}
