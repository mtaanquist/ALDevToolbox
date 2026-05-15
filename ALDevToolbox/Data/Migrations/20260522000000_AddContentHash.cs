using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Adds <c>content_hash</c> to <c>base_app_files</c> — SHA-256 of
    /// content as hex, stamped at import. Powers fast version-to-version
    /// comparison: matching the (ObjectType, ObjectId) pairs between
    /// versions and comparing this column tells us which objects changed
    /// without diffing every body.
    ///
    /// Nullable for back-compat with pre-feature imports. The compare
    /// service treats null on either side as "fall back to content
    /// compare" so legacy versions keep working; users can re-import to
    /// pick up the hash.
    /// </summary>
    public partial class AddContentHash : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "content_hash",
                table: "base_app_files",
                type: "text",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "content_hash",
                table: "base_app_files");
        }
    }
}
