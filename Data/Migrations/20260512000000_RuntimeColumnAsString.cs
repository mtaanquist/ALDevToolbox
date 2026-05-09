using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Promotes <c>runtime_templates.runtime</c> from INTEGER to TEXT so the
    /// column can hold dotted versions like <c>"15.2"</c> as well as the
    /// existing bare majors. Existing INTEGER values coerce cleanly into
    /// their string form via SQLite's loose typing, so no explicit data
    /// rewrite is needed.
    /// </summary>
    public partial class RuntimeColumnAsString : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "runtime",
                table: "runtime_templates",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(int),
                oldType: "INTEGER");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "runtime",
                table: "runtime_templates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(string),
                oldType: "TEXT");
        }
    }
}
