using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddModuleSymbolReferenceContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "symbol_reference_content_hash",
                table: "oe_modules",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_oe_modules_symbol_reference_content_hash",
                table: "oe_modules",
                column: "symbol_reference_content_hash");

            migrationBuilder.AddForeignKey(
                name: "FK_oe_modules_oe_file_contents_symbol_reference_content_hash",
                table: "oe_modules",
                column: "symbol_reference_content_hash",
                principalTable: "oe_file_contents",
                principalColumn: "content_hash",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oe_modules_oe_file_contents_symbol_reference_content_hash",
                table: "oe_modules");

            migrationBuilder.DropIndex(
                name: "IX_oe_modules_symbol_reference_content_hash",
                table: "oe_modules");

            migrationBuilder.DropColumn(
                name: "symbol_reference_content_hash",
                table: "oe_modules");
        }
    }
}
