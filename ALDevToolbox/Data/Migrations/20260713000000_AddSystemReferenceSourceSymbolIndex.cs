using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSystemReferenceSourceSymbolIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_oe_module_system_references_source_symbol_id",
                table: "oe_module_system_references");

            migrationBuilder.CreateIndex(
                name: "ix_oe_module_system_references_source_symbol",
                table: "oe_module_system_references",
                column: "source_symbol_id",
                filter: "\"source_symbol_id\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_oe_module_system_references_source_symbol",
                table: "oe_module_system_references");

            migrationBuilder.CreateIndex(
                name: "IX_oe_module_system_references_source_symbol_id",
                table: "oe_module_system_references",
                column: "source_symbol_id");
        }
    }
}
