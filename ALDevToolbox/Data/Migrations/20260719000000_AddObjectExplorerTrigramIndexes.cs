using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddObjectExplorerTrigramIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_oe_module_symbols_name_trgm",
                table: "oe_module_symbols",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_oe_module_objects_name_trgm",
                table: "oe_module_objects",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_oe_module_objects_version_list_trgm",
                table: "oe_module_objects",
                column: "version_list")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_oe_module_symbols_name_trgm",
                table: "oe_module_symbols");

            migrationBuilder.DropIndex(
                name: "ix_oe_module_objects_name_trgm",
                table: "oe_module_objects");

            migrationBuilder.DropIndex(
                name: "ix_oe_module_objects_version_list_trgm",
                table: "oe_module_objects");
        }
    }
}
