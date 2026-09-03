using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class TranslationMemorySearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_translation_memory_rank",
                table: "translation_memory",
                columns: new[] { "organization_id", "score", "hit_count" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_translation_memory_target_trgm",
                table: "translation_memory",
                column: "target_text")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_translation_memory_rank",
                table: "translation_memory");

            migrationBuilder.DropIndex(
                name: "ix_translation_memory_target_trgm",
                table: "translation_memory");
        }
    }
}
