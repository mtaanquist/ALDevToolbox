using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Adds the Object Explorer tool: a per-org library of Microsoft Base
    /// Application source uploaded as a ZIP per (major, minor, cumulative_update).
    /// Tables: <c>base_app_versions</c>, <c>base_app_files</c>. Trigram GIN
    /// indexes back the fuzzy ILIKE search over file content and object names;
    /// the <c>pg_trgm</c> extension is already enabled by <c>AddSnippets</c>.
    /// </summary>
    public partial class AddObjectExplorer : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "base_app_versions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    major = table.Column<int>(type: "integer", nullable: false),
                    minor = table.Column<int>(type: "integer", nullable: false),
                    cumulative_update = table.Column<int>(type: "integer", nullable: false),
                    application_version_id = table.Column<int>(type: "integer", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    file_count = table.Column<int>(type: "integer", nullable: false),
                    uploaded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_base_app_versions", x => x.id);
                    table.ForeignKey(
                        name: "FK_base_app_versions_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_base_app_versions_application_versions_application_version_id",
                        column: x => x.application_version_id,
                        principalTable: "application_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            // Filtered unique index: one active row per (org, major, minor, cu).
            // Soft-deleted rows are excluded so re-importing the same CU works.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IX_base_app_versions_org_major_minor_cu " +
                "ON base_app_versions (organization_id, major, minor, cumulative_update) " +
                "WHERE deleted_at IS NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_base_app_versions_application_version_id",
                table: "base_app_versions",
                column: "application_version_id");

            migrationBuilder.CreateTable(
                name: "base_app_files",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    version_id = table.Column<int>(type: "integer", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    module = table.Column<string>(type: "text", nullable: true),
                    object_type = table.Column<string>(type: "text", nullable: false),
                    object_id = table.Column<int>(type: "integer", nullable: true),
                    object_name = table.Column<string>(type: "text", nullable: false),
                    @namespace = table.Column<string>(name: "namespace", type: "text", nullable: true),
                    content = table.Column<string>(type: "text", nullable: false),
                    line_count = table.Column<int>(type: "integer", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_base_app_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_base_app_files_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_base_app_files_base_app_versions_version_id",
                        column: x => x.version_id,
                        principalTable: "base_app_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_base_app_files_version_object_type_object_name",
                table: "base_app_files",
                columns: new[] { "version_id", "object_type", "object_name" });

            migrationBuilder.CreateIndex(
                name: "IX_base_app_files_version_object_type_object_id",
                table: "base_app_files",
                columns: new[] { "version_id", "object_type", "object_id" });

            // Trigram GIN indexes power the fuzzy ILIKE search. pg_trgm was
            // enabled by the AddSnippets migration; safe to assume present.
            migrationBuilder.Sql(
                "CREATE INDEX ix_base_app_files_content_trgm ON base_app_files " +
                "USING GIN (lower(content) gin_trgm_ops);");
            migrationBuilder.Sql(
                "CREATE INDEX ix_base_app_files_object_name_trgm ON base_app_files " +
                "USING GIN (lower(object_name) gin_trgm_ops);");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_base_app_files_content_trgm;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_base_app_files_object_name_trgm;");
            migrationBuilder.DropTable("base_app_files");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_base_app_versions_org_major_minor_cu\";");
            migrationBuilder.DropTable("base_app_versions");
        }
    }
}
