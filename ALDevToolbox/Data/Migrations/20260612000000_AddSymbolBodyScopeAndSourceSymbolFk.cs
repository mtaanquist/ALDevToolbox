using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSymbolBodyScopeAndSourceSymbolFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "end_column",
                table: "oe_module_symbols",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "end_line",
                table: "oe_module_symbols",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "source_symbol_id",
                table: "oe_module_references",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_oe_module_references_source_symbol",
                table: "oe_module_references",
                column: "source_symbol_id",
                filter: "\"source_symbol_id\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_oe_module_references_oe_module_symbols_source_symbol_id",
                table: "oe_module_references",
                column: "source_symbol_id",
                principalTable: "oe_module_symbols",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oe_module_references_oe_module_symbols_source_symbol_id",
                table: "oe_module_references");

            migrationBuilder.DropIndex(
                name: "ix_oe_module_references_source_symbol",
                table: "oe_module_references");

            migrationBuilder.DropColumn(
                name: "end_column",
                table: "oe_module_symbols");

            migrationBuilder.DropColumn(
                name: "end_line",
                table: "oe_module_symbols");

            migrationBuilder.DropColumn(
                name: "source_symbol_id",
                table: "oe_module_references");
        }
    }
}
