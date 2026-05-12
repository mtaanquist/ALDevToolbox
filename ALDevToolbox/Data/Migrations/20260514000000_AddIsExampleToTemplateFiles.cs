using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Adds an <c>is_example</c> boolean column to <c>template_files</c> and
    /// <c>template_module_files</c>. Files flagged true are skipped at
    /// generation time when the end user clears "Include example AL files" on
    /// New Workspace / New Extension.
    ///
    /// Existing rows are stamped <c>true</c> so the pre-flag behaviour (every
    /// seeded file was implicitly an example, all-or-nothing on the checkbox)
    /// is preserved. The column default is <c>false</c> so new rows opt in
    /// explicitly via the structured form's per-file checkbox or the
    /// <c>is_example = true</c> line in template TOML.
    /// </summary>
    public partial class AddIsExampleToTemplateFiles : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_example",
                table: "template_files",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_example",
                table: "template_module_files",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Backfill: every previously-seeded file used to be filtered as a
            // group when IncludeExamples was off. Stamp them true so that
            // behaviour survives. New rows continue to default false.
            migrationBuilder.Sql("UPDATE template_files SET is_example = TRUE;");
            migrationBuilder.Sql("UPDATE template_module_files SET is_example = TRUE;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "is_example", table: "template_files");
            migrationBuilder.DropColumn(name: "is_example", table: "template_module_files");
        }
    }
}
