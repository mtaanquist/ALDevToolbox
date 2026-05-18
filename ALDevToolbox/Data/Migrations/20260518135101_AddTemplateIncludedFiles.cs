using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplateIncludedFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "runtime_template_included_files",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    runtime_template_id = table.Column<int>(type: "integer", nullable: false),
                    organization_file_id = table.Column<int>(type: "integer", nullable: false),
                    ordering = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_template_included_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_runtime_template_included_files_organization_files_organiza~",
                        column: x => x.organization_file_id,
                        principalTable: "organization_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_runtime_template_included_files_organizations_organization_~",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_runtime_template_included_files_runtime_templates_runtime_t~",
                        column: x => x.runtime_template_id,
                        principalTable: "runtime_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_runtime_template_included_files_organization_file_id",
                table: "runtime_template_included_files",
                column: "organization_file_id");

            migrationBuilder.CreateIndex(
                name: "IX_runtime_template_included_files_organization_id_runtime_tem~",
                table: "runtime_template_included_files",
                columns: new[] { "organization_id", "runtime_template_id", "ordering" });

            migrationBuilder.CreateIndex(
                name: "IX_runtime_template_included_files_runtime_template_id_organiz~",
                table: "runtime_template_included_files",
                columns: new[] { "runtime_template_id", "organization_file_id" },
                unique: true);

            // Backfill: preserve today's behaviour where every organisation
            // file is emitted by every template. Templates created after the
            // migration start empty and the admin ticks files explicitly.
            migrationBuilder.Sql(@"
                INSERT INTO runtime_template_included_files
                    (organization_id, runtime_template_id, organization_file_id, ordering)
                SELECT
                    t.organization_id,
                    t.id,
                    f.id,
                    f.ordering
                FROM runtime_templates t
                JOIN organization_files f
                    ON f.organization_id = t.organization_id
                WHERE t.deleted_at IS NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "runtime_template_included_files");
        }
    }
}
