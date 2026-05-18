using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddModuleExtensionName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "extension_name",
                table: "modules",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Backfill: strip whitespace and non-alphanumerics from the
            // existing display name so 'Document Capture' becomes
            // 'DocumentCapture'. If the result doesn't start with an
            // uppercase letter, prepend 'M' so the PascalCase pattern is
            // satisfied (the admin can fix it up after the migration).
            migrationBuilder.Sql(@"
                UPDATE modules
                SET extension_name = CASE
                    WHEN regexp_replace(name, '[^A-Za-z0-9]', '', 'g') ~ '^[A-Z]'
                        THEN regexp_replace(name, '[^A-Za-z0-9]', '', 'g')
                    WHEN regexp_replace(name, '[^A-Za-z0-9]', '', 'g') = ''
                        THEN 'Module'
                    ELSE 'M' || regexp_replace(name, '[^A-Za-z0-9]', '', 'g')
                END
                WHERE extension_name = '';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "extension_name",
                table: "modules");
        }
    }
}
