using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Backfills the canonical per-extension <c>app.json</c> as an opt-in
    /// always-included file for every existing organisation and every
    /// existing template. Before this migration, <c>app.json</c> was
    /// constructed in C# (<c>WorkspaceZipBuilder.BuildAppJson</c>); moving
    /// it to an admin-editable <see cref="ALDevToolbox.Domain.Entities.OrganizationFile"/>
    /// means existing orgs/templates need a row + join created retroactively
    /// so their next generation still emits an <c>app.json</c>.
    /// </summary>
    /// <remarks>
    /// Idempotent: both inserts use <c>ON CONFLICT DO NOTHING</c> against the
    /// existing unique indexes (<c>(organization_id, path)</c> on
    /// <c>organization_files</c>; <c>(runtime_template_id, organization_file_id)</c>
    /// on <c>runtime_template_included_files</c>). Re-running the migration
    /// on a database that already has the rows is a no-op.
    /// </remarks>
    public partial class BackfillAppJsonAsIncludedFile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Insert app.json as an EveryExtension org file for every org
            // that doesn't already have one at path 'app.json'. The default
            // content mirrors PlatformOrganizationFiles.DefaultAppJsonContent
            // — admins can edit it on /admin/templates/files post-migration.
            // Postgres dollar-quoting ($content$) lets the JSON body keep its
            // quotes without escaping every double-quote.
            migrationBuilder.Sql(@"
                INSERT INTO organization_files
                    (organization_id, path, content, mustache_enabled, scope, ordering, updated_at)
                SELECT
                    o.id,
                    'app.json',
                    $content${
    ""id"": ""{{extension_id}}"",
    ""name"": ""{{extension_name}}"",
    ""publisher"": ""{{publisher}}"",
    ""version"": ""0.0.0.1"",
    ""brief"": ""{{brief}}"",
    ""description"": ""{{description}}"",
    ""privacyStatement"": """",
    ""EULA"": """",
    ""help"": """",
    ""url"": ""{{url}}"",
    ""logo"": ""{{logo_path}}"",
    ""dependencies"": {{dependencies_array}},
    ""screenshots"": [],
    ""platform"": ""{{platform_version}}"",
    ""application"": ""{{application_version}}"",
    ""target"": ""Cloud"",
    ""idRanges"": {{id_ranges_array}},
    ""resourceExposurePolicy"": {
        ""allowDebugging"": true,
        ""allowDownloadingSource"": true,
        ""includeSourceInSymbolFile"": true
    },
    ""runtime"": ""{{runtime}}"",
    ""features"": [
        ""NoImplicitWith"",
        ""TranslationFile""
    ]
}$content$,
                    true,
                    'EveryExtension',
                    0,
                    NOW()
                FROM organizations o
                ON CONFLICT (organization_id, path) DO NOTHING;
            ");

            // Backfill the per-template join row so every existing template
            // emits the new app.json. Ordering = 0 puts it first in the
            // per-extension emission loop (deterministic ZIP layout). The
            // join lookup is by both organization_id and path so we don't
            // accidentally join a template to another org's file.
            migrationBuilder.Sql(@"
                INSERT INTO runtime_template_included_files
                    (organization_id, runtime_template_id, organization_file_id, ordering)
                SELECT
                    t.organization_id,
                    t.id,
                    f.id,
                    0
                FROM runtime_templates t
                JOIN organization_files f
                    ON f.organization_id = t.organization_id AND f.path = 'app.json'
                ON CONFLICT (runtime_template_id, organization_file_id) DO NOTHING;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Undo the join rows first (FK back to organization_files) then
            // the org files themselves.
            migrationBuilder.Sql(@"
                DELETE FROM runtime_template_included_files
                WHERE organization_file_id IN (
                    SELECT id FROM organization_files WHERE path = 'app.json'
                );
            ");
            migrationBuilder.Sql(@"DELETE FROM organization_files WHERE path = 'app.json';");
        }
    }
}
