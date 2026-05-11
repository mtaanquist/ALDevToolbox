using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Adds the per-organisation default template flag — a single
    /// <c>runtime_templates.is_default</c> boolean column with a filtered
    /// unique index so at most one active default exists per organisation.
    /// </summary>
    /// <remarks>
    /// On migration up we stamp the highest-runtime active template per
    /// organisation as the default so existing organisations don't lose the
    /// "always lands on the same template" behaviour the seed used to give
    /// implicitly. New organisations get the same treatment on first seed
    /// (see <c>SeedService</c>).
    /// </remarks>
    public partial class DefaultTemplate : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_default",
                table: "runtime_templates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Backfill: pick one default per org. The string Runtime column
            // sorts lexicographically, which means "9" outranks "15"; cast
            // the leading numeric segment to int to get version-aware order.
            // Ties broken by id (oldest wins) so the choice is deterministic.
            migrationBuilder.Sql(@"
                WITH ranked AS (
                    SELECT
                        id,
                        ROW_NUMBER() OVER (
                            PARTITION BY organization_id
                            ORDER BY
                                COALESCE(NULLIF(SPLIT_PART(runtime, '.', 1), ''), '0')::int DESC,
                                id ASC
                        ) AS rn
                    FROM runtime_templates
                    WHERE deleted_at IS NULL
                )
                UPDATE runtime_templates t
                SET is_default = true
                FROM ranked r
                WHERE t.id = r.id AND r.rn = 1;
            ");

            migrationBuilder.CreateIndex(
                name: "IX_runtime_templates_organization_id_is_default",
                table: "runtime_templates",
                columns: new[] { "organization_id", "is_default" },
                unique: true,
                filter: "is_default = true AND deleted_at IS NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_runtime_templates_organization_id_is_default",
                table: "runtime_templates");

            migrationBuilder.DropColumn(
                name: "is_default",
                table: "runtime_templates");
        }
    }
}
