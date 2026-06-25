using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSymbolReferenceContentHashIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_oe_modules_symbol_reference_content_hash",
                table: "oe_modules");

            migrationBuilder.CreateIndex(
                name: "ix_oe_modules_symbol_reference_content_hash",
                table: "oe_modules",
                column: "symbol_reference_content_hash",
                filter: "\"symbol_reference_content_hash\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_oe_modules_symbol_reference_content_hash",
                table: "oe_modules");

            migrationBuilder.CreateIndex(
                name: "IX_oe_modules_symbol_reference_content_hash",
                table: "oe_modules",
                column: "symbol_reference_content_hash");
        }
    }
}
