using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class HardenFirstPartyDedupKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_oe_releases_org_label_active",
                table: "oe_releases");

            migrationBuilder.AddColumn<string>(
                name: "dedup_key",
                table: "oe_releases",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // Backfill the explicit dedup key onto already-imported first-party
            // artifact releases so the new unique index protects them without a
            // re-import. Derived from the deterministic label dedup relied on
            // before — "Business Central {Maj}.{Min} ({CC})" → bc-onprem:{Maj}.{Min}:{cc}
            // (country lower-cased). Rows whose label doesn't match (manual uploads,
            // third-party, customer, oddly-named first-party) keep a null key and so
            // stay un-deduped, exactly as intended. See .design/roadmap.md
            // ("Harden first-party dedup, then free the label globally").
            migrationBuilder.Sql("""
                UPDATE oe_releases
                SET dedup_key = 'bc-onprem:'
                    || (regexp_match(label, '^Business Central ([0-9]+\.[0-9]+) \(([A-Za-z0-9]+)\)$'))[1]
                    || ':'
                    || lower((regexp_match(label, '^Business Central ([0-9]+\.[0-9]+) \(([A-Za-z0-9]+)\)$'))[2])
                WHERE kind = 'first_party'
                  AND label ~ '^Business Central [0-9]+\.[0-9]+ \([A-Za-z0-9]+\)$';
                """);

            migrationBuilder.CreateIndex(
                name: "ix_oe_releases_org_dedup_key_active",
                table: "oe_releases",
                columns: new[] { "organization_id", "dedup_key" },
                unique: true,
                filter: "\"deleted_at\" IS NULL AND \"dedup_key\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_oe_releases_org_dedup_key_active",
                table: "oe_releases");

            migrationBuilder.DropColumn(
                name: "dedup_key",
                table: "oe_releases");

            migrationBuilder.CreateIndex(
                name: "ix_oe_releases_org_label_active",
                table: "oe_releases",
                columns: new[] { "organization_id", "label" },
                unique: true,
                filter: "\"deleted_at\" IS NULL AND \"kind\" <> 'customer'");
        }
    }
}
