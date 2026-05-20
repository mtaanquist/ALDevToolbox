using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddModuleTranslations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "oe_module_translations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    module_id = table.Column<long>(type: "bigint", nullable: false),
                    language_code = table.Column<string>(type: "text", nullable: false),
                    trans_unit_id = table.Column<string>(type: "text", nullable: false),
                    source_text = table.Column<string>(type: "text", nullable: false),
                    target_text = table.Column<string>(type: "text", nullable: false),
                    target_state = table.Column<string>(type: "text", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    object_kind = table.Column<string>(type: "text", nullable: true),
                    object_name = table.Column<string>(type: "text", nullable: true),
                    sub_kind = table.Column<string>(type: "text", nullable: true),
                    sub_name = table.Column<string>(type: "text", nullable: true),
                    property_name = table.Column<string>(type: "text", nullable: true),
                    developer_note = table.Column<string>(type: "text", nullable: true),
                    symbol_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_module_translations", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_module_translations_oe_module_symbols_symbol_id",
                        column: x => x.symbol_id,
                        principalTable: "oe_module_symbols",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_oe_module_translations_oe_modules_module_id",
                        column: x => x.module_id,
                        principalTable: "oe_modules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_module_translations_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_oe_module_translations_module_lang",
                table: "oe_module_translations",
                columns: new[] { "module_id", "language_code" });

            migrationBuilder.CreateIndex(
                name: "ix_oe_module_translations_org_obj",
                table: "oe_module_translations",
                columns: new[] { "organization_id", "object_kind", "object_name" });

            migrationBuilder.CreateIndex(
                name: "ix_oe_module_translations_symbol",
                table: "oe_module_translations",
                column: "symbol_id");

            migrationBuilder.CreateIndex(
                name: "ux_oe_module_translations_module_lang_unit",
                table: "oe_module_translations",
                columns: new[] { "module_id", "language_code", "trans_unit_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oe_module_translations");
        }
    }
}
