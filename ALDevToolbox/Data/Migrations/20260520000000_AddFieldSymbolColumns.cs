using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Adds <c>field_id</c> to <c>base_app_symbols</c> so the symbol index can
    /// store table-field declarations (kind = <c>field</c>) and object header
    /// declarations (kind = <c>object_declaration</c>) alongside the existing
    /// procedure / trigger / event rows. The column is nullable because only
    /// the <c>field</c> kind populates it.
    ///
    /// Existing Base App imports keep their symbols; re-running the
    /// <c>SymbolReindexer</c> (or re-importing) backfills the new kinds.
    /// </summary>
    public partial class AddFieldSymbolColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "field_id",
                table: "base_app_symbols",
                type: "integer",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "field_id",
                table: "base_app_symbols");
        }
    }
}
