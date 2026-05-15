using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Creates the new Object Explorer schema for .app symbol-package ingest —
    /// see <c>.design/object-explorer.md</c>. Additive only: the existing
    /// <c>base_app_*</c> tables are left in place. Later PRs in this milestone
    /// migrate the read paths to <c>oe_*</c> and then drop the legacy tables.
    ///
    /// Seven tables, all snake_case:
    ///   oe_releases          one row per imported snapshot of the BC ecosystem
    ///   oe_modules           one row per .app file inside a release
    ///   oe_module_files      .al source kept for the file viewer + scanner
    ///   oe_module_objects    codeunits/tables/pages/etc.
    ///   oe_module_symbols    procedures/fields/triggers/events
    ///   oe_module_variables  object-scoped globals with resolved type
    ///   oe_module_references reference facts (target_app_id triplet),
    ///                        resolved at query time via the parent-chain CTE
    ///
    /// Indexes match the access patterns described in the design doc:
    /// chain walks on <c>parent_release_id</c>, idempotency on
    /// <c>(release_id, app_id, version)</c>, target-triplet lookups on
    /// references and variables for find-references queries.
    /// </summary>
    public partial class AddObjectExplorerTables : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "oe_releases",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    bc_version = table.Column<string>(type: "text", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    parent_release_id = table.Column<int>(type: "integer", nullable: true),
                    application_version_id = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    status_message = table.Column<string>(type: "text", nullable: true),
                    imported_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_releases", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_releases_application_versions_application_version_id",
                        column: x => x.application_version_id,
                        principalTable: "application_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_oe_releases_oe_releases_parent_release_id",
                        column: x => x.parent_release_id,
                        principalTable: "oe_releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_oe_releases_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });


            migrationBuilder.CreateTable(
                name: "oe_modules",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    release_id = table.Column<int>(type: "integer", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    publisher = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false),
                    target = table.Column<string>(type: "text", nullable: true),
                    runtime = table.Column<string>(type: "text", nullable: true),
                    is_test = table.Column<bool>(type: "boolean", nullable: false),
                    is_internal = table.Column<bool>(type: "boolean", nullable: false),
                    is_language_pack = table.Column<bool>(type: "boolean", nullable: false),
                    dependencies_json = table.Column<string>(type: "jsonb", nullable: false),
                    app_file_hash = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_modules", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_modules_oe_releases_release_id",
                        column: x => x.release_id,
                        principalTable: "oe_releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_modules_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });


            migrationBuilder.CreateTable(
                name: "oe_module_files",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    module_id = table.Column<long>(type: "bigint", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    line_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_module_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_module_files_oe_modules_module_id",
                        column: x => x.module_id,
                        principalTable: "oe_modules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_module_files_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });


            migrationBuilder.CreateTable(
                name: "oe_module_objects",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    module_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    object_id = table.Column<int>(type: "integer", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    @namespace = table.Column<string>(name: "namespace", type: "text", nullable: true),
                    extends_app_id = table.Column<Guid>(type: "uuid", nullable: true),
                    extends_object_name = table.Column<string>(type: "text", nullable: true),
                    source_file_id = table.Column<long>(type: "bigint", nullable: true),
                    line_number = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_module_objects", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_module_objects_oe_module_files_source_file_id",
                        column: x => x.source_file_id,
                        principalTable: "oe_module_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_oe_module_objects_oe_modules_module_id",
                        column: x => x.module_id,
                        principalTable: "oe_modules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_module_objects_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });


            migrationBuilder.CreateTable(
                name: "oe_module_symbols",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    module_id = table.Column<long>(type: "bigint", nullable: false),
                    object_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    signature = table.Column<string>(type: "text", nullable: true),
                    return_type = table.Column<string>(type: "text", nullable: true),
                    field_id = table.Column<int>(type: "integer", nullable: true),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    column_start = table.Column<int>(type: "integer", nullable: false),
                    column_end = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_module_symbols", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_module_symbols_oe_module_objects_object_id",
                        column: x => x.object_id,
                        principalTable: "oe_module_objects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_module_symbols_oe_modules_module_id",
                        column: x => x.module_id,
                        principalTable: "oe_modules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_module_symbols_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });


            migrationBuilder.CreateTable(
                name: "oe_module_variables",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    module_id = table.Column<long>(type: "bigint", nullable: false),
                    object_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    type_keyword = table.Column<string>(type: "text", nullable: true),
                    type_name = table.Column<string>(type: "text", nullable: false),
                    target_app_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_object_kind = table.Column<string>(type: "text", nullable: true),
                    target_object_id = table.Column<int>(type: "integer", nullable: true),
                    target_object_name = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_module_variables", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_module_variables_oe_module_objects_object_id",
                        column: x => x.object_id,
                        principalTable: "oe_module_objects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_module_variables_oe_modules_module_id",
                        column: x => x.module_id,
                        principalTable: "oe_modules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_module_variables_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });


            migrationBuilder.CreateTable(
                name: "oe_module_references",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    module_id = table.Column<long>(type: "bigint", nullable: false),
                    source_object_id = table.Column<long>(type: "bigint", nullable: false),
                    target_app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_object_kind = table.Column<string>(type: "text", nullable: false),
                    target_object_id = table.Column<int>(type: "integer", nullable: true),
                    target_object_name = table.Column<string>(type: "text", nullable: false),
                    reference_kind = table.Column<string>(type: "text", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_module_references", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_module_references_oe_module_objects_source_object_id",
                        column: x => x.source_object_id,
                        principalTable: "oe_module_objects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_module_references_oe_modules_module_id",
                        column: x => x.module_id,
                        principalTable: "oe_modules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_module_references_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });


            migrationBuilder.CreateIndex(
                name: "ix_oe_module_files_module_path",
                table: "oe_module_files",
                columns: new[] { "module_id", "path" },
                unique: true);


            migrationBuilder.CreateIndex(
                name: "IX_oe_module_files_organization_id",
                table: "oe_module_files",
                column: "organization_id");


            migrationBuilder.CreateIndex(
                name: "ix_oe_module_objects_module_kind_name",
                table: "oe_module_objects",
                columns: new[] { "module_id", "kind", "name" });


            migrationBuilder.CreateIndex(
                name: "ix_oe_module_objects_module_kind_objectid",
                table: "oe_module_objects",
                columns: new[] { "module_id", "kind", "object_id" });


            migrationBuilder.CreateIndex(
                name: "IX_oe_module_objects_organization_id",
                table: "oe_module_objects",
                column: "organization_id");


            migrationBuilder.CreateIndex(
                name: "IX_oe_module_objects_source_file_id",
                table: "oe_module_objects",
                column: "source_file_id");


            migrationBuilder.CreateIndex(
                name: "IX_oe_module_references_module_id",
                table: "oe_module_references",
                column: "module_id");


            migrationBuilder.CreateIndex(
                name: "IX_oe_module_references_organization_id",
                table: "oe_module_references",
                column: "organization_id");


            migrationBuilder.CreateIndex(
                name: "ix_oe_module_references_source_object",
                table: "oe_module_references",
                column: "source_object_id");


            migrationBuilder.CreateIndex(
                name: "ix_oe_module_references_target_id",
                table: "oe_module_references",
                columns: new[] { "target_app_id", "target_object_kind", "target_object_id" });


            migrationBuilder.CreateIndex(
                name: "ix_oe_module_references_target_name",
                table: "oe_module_references",
                columns: new[] { "target_app_id", "target_object_kind", "target_object_name" });


            migrationBuilder.CreateIndex(
                name: "ix_oe_module_symbols_module_kind_name",
                table: "oe_module_symbols",
                columns: new[] { "module_id", "kind", "name" });


            migrationBuilder.CreateIndex(
                name: "ix_oe_module_symbols_object_line",
                table: "oe_module_symbols",
                columns: new[] { "object_id", "line_number" });


            migrationBuilder.CreateIndex(
                name: "IX_oe_module_symbols_organization_id",
                table: "oe_module_symbols",
                column: "organization_id");


            migrationBuilder.CreateIndex(
                name: "IX_oe_module_variables_module_id",
                table: "oe_module_variables",
                column: "module_id");


            migrationBuilder.CreateIndex(
                name: "ix_oe_module_variables_object_name",
                table: "oe_module_variables",
                columns: new[] { "object_id", "name" });


            migrationBuilder.CreateIndex(
                name: "IX_oe_module_variables_organization_id",
                table: "oe_module_variables",
                column: "organization_id");


            migrationBuilder.CreateIndex(
                name: "ix_oe_module_variables_target_id",
                table: "oe_module_variables",
                columns: new[] { "target_app_id", "target_object_kind", "target_object_id" });


            migrationBuilder.CreateIndex(
                name: "ix_oe_module_variables_target_name",
                table: "oe_module_variables",
                columns: new[] { "target_app_id", "target_object_kind", "target_object_name" });


            migrationBuilder.CreateIndex(
                name: "ix_oe_modules_appid",
                table: "oe_modules",
                column: "app_id");


            migrationBuilder.CreateIndex(
                name: "IX_oe_modules_organization_id",
                table: "oe_modules",
                column: "organization_id");


            migrationBuilder.CreateIndex(
                name: "ix_oe_modules_release_appid_version",
                table: "oe_modules",
                columns: new[] { "release_id", "app_id", "version" },
                unique: true);


            migrationBuilder.CreateIndex(
                name: "IX_oe_releases_application_version_id",
                table: "oe_releases",
                column: "application_version_id");


            migrationBuilder.CreateIndex(
                name: "ix_oe_releases_org_label_active",
                table: "oe_releases",
                columns: new[] { "organization_id", "label" },
                unique: true,
                filter: "\"deleted_at\" IS NULL");


            migrationBuilder.CreateIndex(
                name: "ix_oe_releases_parent",
                table: "oe_releases",
                column: "parent_release_id");


        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop in reverse-FK order. The base_app_* tables are untouched.
            migrationBuilder.DropTable(name: "oe_module_references");
            migrationBuilder.DropTable(name: "oe_module_symbols");
            migrationBuilder.DropTable(name: "oe_module_variables");
            migrationBuilder.DropTable(name: "oe_module_objects");
            migrationBuilder.DropTable(name: "oe_module_files");
            migrationBuilder.DropTable(name: "oe_modules");
            migrationBuilder.DropTable(name: "oe_releases");
        }
    }
}
