using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Adds the <c>base_app_extensions</c> table — one row per imported
    /// extension's <c>app.json</c> — and an <c>extension_id</c> FK on
    /// <c>base_app_files</c>. The FK is nullable because legacy imports
    /// (and any future ZIP that doesn't carry an <c>app.json</c>) leave
    /// files unattributed; the Object Explorer renders blank in that case.
    ///
    /// Uniqueness on <c>(version_id, app_id)</c> means re-importing the
    /// same app (matched by its GUID) into the same version reuses the
    /// existing row rather than spawning a duplicate.
    /// </summary>
    public partial class AddBaseAppExtensions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "base_app_extensions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    version_id = table.Column<int>(type: "integer", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    publisher = table.Column<string>(type: "text", nullable: false),
                    app_version = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_base_app_extensions", x => x.id);
                    table.ForeignKey(
                        name: "FK_base_app_extensions_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_base_app_extensions_base_app_versions_version_id",
                        column: x => x.version_id,
                        principalTable: "base_app_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_base_app_extensions_version_app",
                table: "base_app_extensions",
                columns: new[] { "version_id", "app_id" },
                unique: true);

            migrationBuilder.AddColumn<long>(
                name: "extension_id",
                table: "base_app_files",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_base_app_files_base_app_extensions_extension_id",
                table: "base_app_files",
                column: "extension_id",
                principalTable: "base_app_extensions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.CreateIndex(
                name: "IX_base_app_files_version_extension",
                table: "base_app_files",
                columns: new[] { "version_id", "extension_id" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_base_app_files_version_extension",
                table: "base_app_files");

            migrationBuilder.DropForeignKey(
                name: "FK_base_app_files_base_app_extensions_extension_id",
                table: "base_app_files");

            migrationBuilder.DropColumn(
                name: "extension_id",
                table: "base_app_files");

            migrationBuilder.DropTable("base_app_extensions");
        }
    }
}
