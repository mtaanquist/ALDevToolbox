using ALDevToolbox.Data.Migrations;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ALDevToolbox.Tests.Migrations;

/// <summary>
/// Pins the data-rewrite half of <see cref="UnifyExtensions"/>. The acceptance
/// criterion for Issue #54 calls out a test that proves the PL/pgSQL block
/// produces the shape it claims when pre-unified rows are present.
///
/// Approach: the fixture's <see cref="DatabaseFacade.MigrateAsync"/> already
/// ran (so the new tables exist and the pre-unified tables are gone). We
/// recreate just enough of the pre-unified schema to seed a fixture row in
/// each (<c>template_folders</c> / <c>template_files</c> /
/// <c>template_module_folders</c> / <c>template_module_files</c>, plus the
/// dropped <c>default_application</c> / <c>default_platform</c> columns on
/// <c>runtime_templates</c>), then replay <see cref="UnifyExtensions.DataRewriteSql"/>
/// and assert the rewritten shape.
/// </summary>
/// <remarks>
/// The SQL is the contract; this test calls <see cref="UnifyExtensions.DataRewriteSql"/>
/// rather than copy-pasting the block so a regression in the migration body
/// trips the test rather than diverging silently. Parameterised inserts avoid
/// the C# raw-interpolated-string brace-escape minefield (a single dollar sign
/// makes <c>{</c> the interp marker, so <c>{{prefix}}</c> mustache content
/// wouldn't survive raw-string interpolation).
/// </remarks>
public sealed class UnifyExtensionsDataMigrationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Replaying_the_data_rewrite_block_lifts_preunified_rows_into_the_unified_shape()
    {
        // 1) Recreate the pre-unified table shapes + dropped columns. We
        //    deliberately use the minimal columns the rewrite SQL reads:
        //    additional pre-unified columns aren't needed for this test.
        await ExecuteAsync("""
            ALTER TABLE runtime_templates ADD COLUMN default_application text;
            ALTER TABLE runtime_templates ADD COLUMN default_platform text;

            CREATE TABLE template_folders (
                id              serial PRIMARY KEY,
                organization_id integer NOT NULL,
                template_id     integer NOT NULL REFERENCES runtime_templates(id) ON DELETE CASCADE,
                ordering        integer NOT NULL,
                path            text    NOT NULL
            );
            CREATE TABLE template_files (
                id                  serial PRIMARY KEY,
                organization_id     integer NOT NULL,
                template_folder_id  integer NOT NULL REFERENCES template_folders(id) ON DELETE CASCADE,
                ordering            integer NOT NULL,
                path                text    NOT NULL,
                content             text    NOT NULL
            );
            CREATE TABLE template_module_folders (
                id              serial PRIMARY KEY,
                organization_id integer NOT NULL,
                template_id     integer NOT NULL REFERENCES runtime_templates(id) ON DELETE CASCADE,
                ordering        integer NOT NULL,
                path            text    NOT NULL
            );
            CREATE TABLE template_module_files (
                id                         serial PRIMARY KEY,
                organization_id            integer NOT NULL,
                template_module_folder_id  integer NOT NULL REFERENCES template_module_folders(id) ON DELETE CASCADE,
                ordering                   integer NOT NULL,
                path                       text    NOT NULL,
                content                    text    NOT NULL
            );
            """);

        // 2) Seed fixture rows. One template with one folder containing a
        //    file that exercises the {{prefix}} → {{affix}} rewrite, one
        //    module hooked up to the template via the default-modules join,
        //    and one module-folder + file the rewrite should clone onto the
        //    module side. Slash-separated path forces the recursive split
        //    into two folder rows.
        var orgId = TestDb.DefaultOrgId;

        var templateId = await ScalarIntAsync("""
            INSERT INTO runtime_templates (
                organization_id, key, runtime, name, description, defaults_json,
                app_source_cop_json, core_id_range_from, core_id_range_to,
                module_id_range_start, module_id_range_size, deprecated, is_default,
                default_application, default_platform, created_at, updated_at)
            VALUES (
                @org, 'preunified', '15', 'Pre-Unified Template', NULL,
                '{}'::jsonb,
                '{"mandatoryPrefix":"PRE"}'::jsonb,
                90000, 90999, 91000, 200, FALSE, FALSE,
                '27.0.0.0', '1.0.0.0', NOW(), NOW())
            RETURNING id;
            """,
            ("org", orgId));

        var moduleId = await ScalarIntAsync("""
            INSERT INTO modules (organization_id, key, name, created_at, updated_at)
            VALUES (@org, 'preunified-module', 'Pre-Unified Module', NOW(), NOW())
            RETURNING id;
            """,
            ("org", orgId));

        await ExecuteAsync("""
            INSERT INTO runtime_template_default_modules
                (organization_id, runtime_template_id, module_id, ordering)
            VALUES (@org, @templateId, @moduleId, 0);
            """,
            ("org", orgId), ("templateId", templateId), ("moduleId", moduleId));

        var folderId = await ScalarIntAsync("""
            INSERT INTO template_folders (organization_id, template_id, ordering, path)
            VALUES (@org, @templateId, 0, 'src/codeunits')
            RETURNING id;
            """,
            ("org", orgId), ("templateId", templateId));

        await ExecuteAsync("""
            INSERT INTO template_files (organization_id, template_folder_id, ordering, path, content)
            VALUES (@org, @folderId, 0, 'AppInstall.al', @content);
            """,
            ("org", orgId),
            ("folderId", folderId),
            ("content", "codeunit 90000 \"{{prefix}} App Install\" { Subtype = Install; }"));

        var modFolderId = await ScalarIntAsync("""
            INSERT INTO template_module_folders (organization_id, template_id, ordering, path)
            VALUES (@org, @templateId, 0, 'src/api')
            RETURNING id;
            """,
            ("org", orgId), ("templateId", templateId));

        await ExecuteAsync("""
            INSERT INTO template_module_files (organization_id, template_module_folder_id, ordering, path, content)
            VALUES (@org, @folderId, 0, 'Adapter.al', @content);
            """,
            ("org", orgId),
            ("folderId", modFolderId),
            ("content", "codeunit 91000 \"{{prefix}} Adapter\" { }"));

        // 3) Replay the migration's data block verbatim.
        await ExecuteAsync(UnifyExtensions.DataRewriteSql);

        // 4) Assert: each pre-unified shape lifted into the unified one.
        await using var ctx = _db.NewContext();

        // 4a) One Core workspace_extension per template, marked required, with
        //     the canonical mustache name template.
        var extensions = await ctx.WorkspaceExtensions
            .Where(e => e.TemplateId == templateId)
            .AsNoTracking()
            .ToListAsync();
        extensions.Should().ContainSingle();
        extensions.Single().Path.Should().Be("Core");
        extensions.Single().Required.Should().BeTrue();
        extensions.Single().NameTemplate.Should().Be("{{extension_prefix}} Core");
        var coreId = extensions.Single().Id;

        // 4b) The slash-separated folder path splits into two rows in the
        //     recursive tree — 'src' at the root, 'codeunits' under it.
        var folders = await ctx.WorkspaceExtensionFolders
            .Where(f => f.WorkspaceExtensionId == coreId)
            .AsNoTracking()
            .ToListAsync();
        folders.Should().HaveCount(2);
        var root = folders.Single(f => f.ParentFolderId == null);
        root.Path.Should().Be("src");
        var leaf = folders.Single(f => f.ParentFolderId == root.Id);
        leaf.Path.Should().Be("codeunits");

        // 4c) File attaches to the leaf folder, and {{prefix}} → {{affix}}
        //     rewrites in place.
        var files = await ctx.WorkspaceExtensionFiles
            .Where(f => f.WorkspaceExtensionFolderId == leaf.Id)
            .AsNoTracking()
            .ToListAsync();
        files.Should().ContainSingle();
        files.Single().Path.Should().Be("AppInstall.al");
        files.Single().Content.Should().Contain("{{affix}} App Install")
            .And.NotContain("{{prefix}}", "mustache placeholders should be unified");

        // 4d) Module folder/file rows cloned onto the module side.
        var moduleFolders = await ctx.ModuleExtensionFolders
            .Where(f => f.ModuleId == moduleId)
            .AsNoTracking()
            .ToListAsync();
        moduleFolders.Should().HaveCount(2);
        var modRoot = moduleFolders.Single(f => f.ParentFolderId == null);
        modRoot.Path.Should().Be("src");
        var modLeaf = moduleFolders.Single(f => f.ParentFolderId == modRoot.Id);
        modLeaf.Path.Should().Be("api");

        var moduleFiles = await ctx.ModuleExtensionFiles
            .Where(f => f.ModuleExtensionFolderId == modLeaf.Id)
            .AsNoTracking()
            .ToListAsync();
        moduleFiles.Should().ContainSingle();
        moduleFiles.Single().Content.Should().Contain("{{affix}} Adapter");

        // 4e) defaults_json folds in default_application / default_platform,
        //     and stamps affix / affixType from AppSourceCop.mandatoryPrefix.
        var defaultsJson = await ScalarStringAsync(
            "SELECT defaults_json::text FROM runtime_templates WHERE id = @id",
            ("id", templateId));
        defaultsJson.Should().Contain("\"application\": \"27.0.0.0\"");
        defaultsJson.Should().Contain("\"platform\": \"1.0.0.0\"");
        defaultsJson.Should().Contain("\"extension_prefix\": \"PRE\"");
        defaultsJson.Should().Contain("\"affix\": \"PRE\"");
        defaultsJson.Should().Contain("\"affixType\": \"Prefix\"");
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> ScalarIntAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<string> ScalarStringAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
}
