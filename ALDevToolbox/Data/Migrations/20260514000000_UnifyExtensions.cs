using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Unifies the Core extension and modules under a single
    /// <c>[[extensions]]</c> data model. Drops the four-way split
    /// (<c>template_folders</c> / <c>template_files</c> /
    /// <c>template_module_folders</c> / <c>template_module_files</c>) in favour
    /// of <c>workspace_extensions</c> + recursive
    /// <c>workspace_extension_folders</c> + per-folder
    /// <c>workspace_extension_files</c> + per-extension
    /// <c>workspace_extension_dependencies</c>, plus mirror tables on the
    /// catalogue side (<c>module_extension_folders</c>,
    /// <c>module_extension_files</c>) so modules carry their own extension
    /// layout instead of sharing one template-wide scaffold.
    ///
    /// Also folds the free-text <c>default_application</c> /
    /// <c>default_platform</c> columns into the <c>defaults_json</c> blob (as
    /// <c>application</c> / <c>platform</c>) and rewrites mustache
    /// <c>{{prefix}}</c> / <c>{{suffix}}</c> placeholders to the new unified
    /// <c>{{affix}}</c>.
    ///
    /// See <c>.design/unified-extensions.md</c> for the design and
    /// <c>.design/templates-and-seeding.md</c> for the new TOML shape.
    /// </summary>
    /// <remarks>
    /// The migration is forward-only. The pre-unified shape can't be restored
    /// losslessly: the per-module folder/file rows have to be collapsed back
    /// into a single template-wide shared set, *and* the folder tree
    /// re-flattened into slash-separated paths — both information-destroying.
    /// <see cref="Down"/> therefore throws.
    /// </remarks>
    public partial class UnifyExtensions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) New tables.

            migrationBuilder.CreateTable(
                name: "workspace_extensions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    template_id = table.Column<int>(type: "integer", nullable: false),
                    ordering = table.Column<int>(type: "integer", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    name_template = table.Column<string>(type: "text", nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    application = table.Column<string>(type: "text", nullable: true),
                    runtime = table.Column<string>(type: "text", nullable: true),
                    id_range_from = table.Column<int>(type: "integer", nullable: true),
                    id_range_to = table.Column<int>(type: "integer", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_extensions", x => x.id);
                    table.ForeignKey(
                        name: "FK_workspace_extensions_runtime_templates_template_id",
                        column: x => x.template_id,
                        principalTable: "runtime_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workspace_extensions_organization_id_template_id_ordering",
                table: "workspace_extensions",
                columns: new[] { "organization_id", "template_id", "ordering" });

            migrationBuilder.CreateIndex(
                name: "IX_workspace_extensions_template_id_path",
                table: "workspace_extensions",
                columns: new[] { "template_id", "path" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "workspace_extension_folders",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    workspace_extension_id = table.Column<int>(type: "integer", nullable: false),
                    parent_folder_id = table.Column<int>(type: "integer", nullable: true),
                    ordering = table.Column<int>(type: "integer", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_extension_folders", x => x.id);
                    table.ForeignKey(
                        name: "FK_workspace_extension_folders_workspace_extensions_workspace_extension_id",
                        column: x => x.workspace_extension_id,
                        principalTable: "workspace_extensions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_workspace_extension_folders_self_parent_folder_id",
                        column: x => x.parent_folder_id,
                        principalTable: "workspace_extension_folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workspace_extension_folders_workspace_extension_id_parent_folder_id_ordering",
                table: "workspace_extension_folders",
                columns: new[] { "workspace_extension_id", "parent_folder_id", "ordering" });

            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_workspace_extension_folders_sibling_unique " +
                "ON workspace_extension_folders (parent_folder_id, path) WHERE parent_folder_id IS NOT NULL;");

            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_workspace_extension_folders_root_unique " +
                "ON workspace_extension_folders (workspace_extension_id, path) WHERE parent_folder_id IS NULL;");

            migrationBuilder.CreateTable(
                name: "workspace_extension_files",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    workspace_extension_folder_id = table.Column<int>(type: "integer", nullable: false),
                    ordering = table.Column<int>(type: "integer", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    is_example = table.Column<bool>(type: "boolean", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_extension_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_workspace_extension_files_workspace_extension_folders_workspace_extension_folder_id",
                        column: x => x.workspace_extension_folder_id,
                        principalTable: "workspace_extension_folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workspace_extension_files_workspace_extension_folder_id_ordering",
                table: "workspace_extension_files",
                columns: new[] { "workspace_extension_folder_id", "ordering" });

            migrationBuilder.CreateIndex(
                name: "IX_workspace_extension_files_workspace_extension_folder_id_path",
                table: "workspace_extension_files",
                columns: new[] { "workspace_extension_folder_id", "path" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "workspace_extension_dependencies",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    workspace_extension_id = table.Column<int>(type: "integer", nullable: false),
                    ordering = table.Column<int>(type: "integer", nullable: false),
                    ref_extension_path = table.Column<string>(type: "text", nullable: true),
                    ref_module_key = table.Column<string>(type: "text", nullable: true),
                    lit_id = table.Column<string>(type: "text", nullable: true),
                    lit_name = table.Column<string>(type: "text", nullable: true),
                    lit_publisher = table.Column<string>(type: "text", nullable: true),
                    lit_version = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_extension_dependencies", x => x.id);
                    table.ForeignKey(
                        name: "FK_workspace_extension_dependencies_workspace_extensions_workspace_extension_id",
                        column: x => x.workspace_extension_id,
                        principalTable: "workspace_extensions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workspace_extension_dependencies_workspace_extension_id_ordering",
                table: "workspace_extension_dependencies",
                columns: new[] { "workspace_extension_id", "ordering" });

            // Exactly one of the three reference shapes must be set. Postgres
            // checks the constraint per-row; the service layer pre-validates so
            // bad shapes never reach the DB in the happy path.
            migrationBuilder.Sql(
                "ALTER TABLE workspace_extension_dependencies " +
                "ADD CONSTRAINT ck_workspace_extension_dependencies_one_ref CHECK ( " +
                "  (CASE WHEN ref_extension_path IS NOT NULL THEN 1 ELSE 0 END) + " +
                "  (CASE WHEN ref_module_key   IS NOT NULL THEN 1 ELSE 0 END) + " +
                "  (CASE WHEN lit_id           IS NOT NULL THEN 1 ELSE 0 END) " +
                "= 1);");

            migrationBuilder.CreateTable(
                name: "module_extension_folders",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    module_id = table.Column<int>(type: "integer", nullable: false),
                    parent_folder_id = table.Column<int>(type: "integer", nullable: true),
                    ordering = table.Column<int>(type: "integer", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_module_extension_folders", x => x.id);
                    table.ForeignKey(
                        name: "FK_module_extension_folders_modules_module_id",
                        column: x => x.module_id,
                        principalTable: "modules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_module_extension_folders_self_parent_folder_id",
                        column: x => x.parent_folder_id,
                        principalTable: "module_extension_folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_module_extension_folders_module_id_parent_folder_id_ordering",
                table: "module_extension_folders",
                columns: new[] { "module_id", "parent_folder_id", "ordering" });

            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_module_extension_folders_sibling_unique " +
                "ON module_extension_folders (parent_folder_id, path) WHERE parent_folder_id IS NOT NULL;");

            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_module_extension_folders_root_unique " +
                "ON module_extension_folders (module_id, path) WHERE parent_folder_id IS NULL;");

            migrationBuilder.CreateTable(
                name: "module_extension_files",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    module_extension_folder_id = table.Column<int>(type: "integer", nullable: false),
                    ordering = table.Column<int>(type: "integer", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    is_example = table.Column<bool>(type: "boolean", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_module_extension_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_module_extension_files_module_extension_folders_module_extension_folder_id",
                        column: x => x.module_extension_folder_id,
                        principalTable: "module_extension_folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_module_extension_files_module_extension_folder_id_ordering",
                table: "module_extension_files",
                columns: new[] { "module_extension_folder_id", "ordering" });

            migrationBuilder.CreateIndex(
                name: "IX_module_extension_files_module_extension_folder_id_path",
                table: "module_extension_files",
                columns: new[] { "module_extension_folder_id", "path" },
                unique: true);

            // 2) Data migration. PL/pgSQL block: per-template Core extension,
            //    folder-tree split on '/', file copy, then per-template-module
            //    fan-out into module_extension_folders. The function is local
            //    to this transaction.

            migrationBuilder.Sql(@"
DO $unify$
DECLARE
    tmpl RECORD;
    fld  RECORD;
    f    RECORD;
    seg  TEXT;
    segs TEXT[];
    parent_id INT;
    leaf_id   INT;
    ext_id    INT;
    next_ord  INT;
    mod_row   RECORD;
    mod_fld   RECORD;
    mod_file  RECORD;
    mod_parent INT;
    mod_leaf   INT;
BEGIN
    -- Per-template: create the Core workspace_extension, walk template_folders
    -- into the recursive tree, copy template_files into the leaf folders.
    FOR tmpl IN SELECT id, organization_id FROM runtime_templates LOOP
        INSERT INTO workspace_extensions
            (organization_id, template_id, ordering, path, name_template, required,
             application, runtime, id_range_from, id_range_to)
        VALUES
            (tmpl.organization_id, tmpl.id, 0, 'Core',
             '{{extension_prefix}} Core', TRUE, NULL, NULL, NULL, NULL)
        RETURNING id INTO ext_id;

        FOR fld IN
            SELECT id, organization_id, ordering, path
            FROM template_folders
            WHERE template_id = tmpl.id
            ORDER BY ordering
        LOOP
            segs := string_to_array(fld.path, '/');
            parent_id := NULL;
            next_ord := fld.ordering;
            FOR i IN 1 .. array_length(segs, 1) LOOP
                seg := segs[i];
                IF seg IS NULL OR seg = '' THEN CONTINUE; END IF;

                -- Reuse an existing sibling folder with the same name. The
                -- ordering for newly-created intermediate rows follows the
                -- triggering top-level folder's ordering; collisions resolve
                -- to the earliest insertion.
                IF parent_id IS NULL THEN
                    SELECT id INTO leaf_id FROM workspace_extension_folders
                    WHERE workspace_extension_id = ext_id
                      AND parent_folder_id IS NULL
                      AND path = seg
                    LIMIT 1;
                ELSE
                    SELECT id INTO leaf_id FROM workspace_extension_folders
                    WHERE parent_folder_id = parent_id
                      AND path = seg
                    LIMIT 1;
                END IF;

                IF leaf_id IS NULL THEN
                    INSERT INTO workspace_extension_folders
                        (organization_id, workspace_extension_id, parent_folder_id, ordering, path)
                    VALUES
                        (fld.organization_id, ext_id, parent_id, next_ord, seg)
                    RETURNING id INTO leaf_id;
                END IF;
                parent_id := leaf_id;
            END LOOP;

            -- Files for this row attach to the leaf produced above.
            FOR f IN
                SELECT organization_id, ordering, path, content
                FROM template_files
                WHERE template_folder_id = fld.id
                ORDER BY ordering
            LOOP
                INSERT INTO workspace_extension_files
                    (organization_id, workspace_extension_folder_id, ordering, path, content, is_example)
                VALUES
                    (f.organization_id, leaf_id, f.ordering, f.path, f.content, FALSE);
            END LOOP;
        END LOOP;
    END LOOP;

    -- Modules: today's template_module_folders are shared per template across
    -- every module the template default-selects. Lossy step: we duplicate the
    -- folder/file rows onto every module in each template's default_modules
    -- list. Modules that aren't in any default list end up empty — admins
    -- fix them up afterwards.
    FOR mod_row IN
        SELECT DISTINCT m.id AS module_id, m.organization_id, dm.runtime_template_id
        FROM modules m
        JOIN runtime_template_default_modules dm ON dm.module_id = m.id
    LOOP
        FOR mod_fld IN
            SELECT id, organization_id, ordering, path
            FROM template_module_folders
            WHERE template_id = mod_row.runtime_template_id
            ORDER BY ordering
        LOOP
            segs := string_to_array(mod_fld.path, '/');
            mod_parent := NULL;
            next_ord := mod_fld.ordering;
            FOR i IN 1 .. array_length(segs, 1) LOOP
                seg := segs[i];
                IF seg IS NULL OR seg = '' THEN CONTINUE; END IF;

                IF mod_parent IS NULL THEN
                    SELECT id INTO mod_leaf FROM module_extension_folders
                    WHERE module_id = mod_row.module_id
                      AND parent_folder_id IS NULL
                      AND path = seg
                    LIMIT 1;
                ELSE
                    SELECT id INTO mod_leaf FROM module_extension_folders
                    WHERE parent_folder_id = mod_parent
                      AND path = seg
                    LIMIT 1;
                END IF;

                IF mod_leaf IS NULL THEN
                    INSERT INTO module_extension_folders
                        (organization_id, module_id, parent_folder_id, ordering, path)
                    VALUES
                        (mod_fld.organization_id, mod_row.module_id, mod_parent, next_ord, seg)
                    RETURNING id INTO mod_leaf;
                END IF;
                mod_parent := mod_leaf;
            END LOOP;

            FOR mod_file IN
                SELECT organization_id, ordering, path, content
                FROM template_module_files
                WHERE template_module_folder_id = mod_fld.id
                ORDER BY ordering
            LOOP
                INSERT INTO module_extension_files
                    (organization_id, module_extension_folder_id, ordering, path, content, is_example)
                VALUES
                    (mod_file.organization_id, mod_leaf, mod_file.ordering, mod_file.path, mod_file.content, FALSE)
                -- A module can be default-selected by two templates with
                -- overlapping module-folder paths; the unique index would
                -- complain on a collision. Newer rows lose.
                ON CONFLICT (module_extension_folder_id, path) DO NOTHING;
            END LOOP;
        END LOOP;
    END LOOP;

    -- Defaults: fold the free-text default_application / default_platform
    -- columns into the jsonb defaults blob. Empty/null values become empty
    -- strings so the typed value object's required-string contract is
    -- satisfied.
    UPDATE runtime_templates
    SET defaults_json = jsonb_set(
        jsonb_set(
            jsonb_set(
                jsonb_set(
                    COALESCE(defaults_json, '{}'::jsonb),
                    '{application}', to_jsonb(COALESCE(default_application, '')), TRUE),
                '{platform}', to_jsonb(COALESCE(default_platform, '')), TRUE),
            '{extension_prefix}',
            -- Carry the existing AppSourceCop mandatoryPrefix as the default
            -- extension prefix when the template had one; pre-unified models
            -- effectively conflated the two anyway. Empty string when missing.
            to_jsonb(COALESCE(app_source_cop_json->>'mandatoryPrefix', '')),
            TRUE),
        '{affix}',
        to_jsonb(COALESCE(app_source_cop_json->>'mandatoryPrefix', '')),
        TRUE);

    -- Set affixType to ""Prefix"" when the mandatoryPrefix was non-empty, ""None"" otherwise.
    UPDATE runtime_templates
    SET defaults_json = jsonb_set(
        defaults_json,
        '{affixType}',
        CASE
            WHEN COALESCE(app_source_cop_json->>'mandatoryPrefix', '') = '' THEN to_jsonb('None'::text)
            ELSE to_jsonb('Prefix'::text)
        END,
        TRUE);

    -- Mustache rewrite: {{prefix}} / {{suffix}} → {{affix}}. Pre-unified
    -- templates used {{prefix}} for AL object name prefixes; the unified
    -- model collapses both to {{affix}}.
    UPDATE workspace_extension_files
    SET content = regexp_replace(
            regexp_replace(content, '\{\{prefix\}\}',  '{{affix}}', 'g'),
            '\{\{suffix\}\}', '{{affix}}', 'g')
    WHERE content LIKE '%{{prefix}}%' OR content LIKE '%{{suffix}}%';

    UPDATE module_extension_files
    SET content = regexp_replace(
            regexp_replace(content, '\{\{prefix\}\}',  '{{affix}}', 'g'),
            '\{\{suffix\}\}', '{{affix}}', 'g')
    WHERE content LIKE '%{{prefix}}%' OR content LIKE '%{{suffix}}%';

    -- Organisation files are mustache-substituted by the generator too; rewrite
    -- their content for the same reason.
    UPDATE organization_files
    SET content = regexp_replace(
            regexp_replace(content, '\{\{prefix\}\}',  '{{affix}}', 'g'),
            '\{\{suffix\}\}', '{{affix}}', 'g')
    WHERE content LIKE '%{{prefix}}%' OR content LIKE '%{{suffix}}%';
END
$unify$;
");

            // 3) Drop the now-redundant columns and tables.

            migrationBuilder.DropTable(name: "template_files");
            migrationBuilder.DropTable(name: "template_folders");
            migrationBuilder.DropTable(name: "template_module_files");
            migrationBuilder.DropTable(name: "template_module_folders");

            migrationBuilder.DropColumn(name: "default_application", table: "runtime_templates");
            migrationBuilder.DropColumn(name: "default_platform", table: "runtime_templates");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restoring the pre-unified shape would require collapsing every
            // module's per-row folder/file tree back into a single template-
            // wide scaffold (lossy when two modules diverge), *and* re-
            // flattening the recursive folder tree into slash-separated paths
            // (lossy when intermediate folders carry files). We don't claim
            // either round-trip; rolling back this migration is unsupported.
            throw new NotSupportedException(
                "Migration 20260514000000_UnifyExtensions is forward-only. " +
                "Restore from backup if you need the pre-unified shape.");
        }
    }
}
