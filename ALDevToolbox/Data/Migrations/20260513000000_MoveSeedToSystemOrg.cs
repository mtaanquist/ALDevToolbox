using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Retires the on-disk <c>Templates.seed/</c> bootstrap. The Default
    /// organisation is now the canonical "system" org that hosts the templates,
    /// modules and application versions other orgs fork from via
    /// <c>TemplateImportService</c>; regular orgs start empty.
    ///
    /// Renames <c>organizations.is_seeded</c> to <c>is_system</c>, stamps the
    /// Default row (<c>slug = 'default'</c>) as the system org, and installs a
    /// partial unique index that refuses a second system org per deployment.
    /// Existing org rows that were previously marked <c>is_seeded = true</c>
    /// (i.e. populated by the old <c>SeedService</c>) collapse to plain
    /// <c>is_system = false</c> — their per-org template content stays
    /// untouched.
    /// </summary>
    public partial class MoveSeedToSystemOrg : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Reuse the existing boolean column for the new semantic. Wipe its
            // values first so any previously-seeded orgs don't appear as the
            // system org by accident; the next statement re-stamps the Default
            // row only.
            migrationBuilder.RenameColumn(
                name: "is_seeded",
                table: "organizations",
                newName: "is_system");
            migrationBuilder.Sql("UPDATE organizations SET is_system = false;");
            migrationBuilder.Sql(
                "UPDATE organizations SET is_system = true WHERE slug = 'default';");

            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_organizations_is_system_singleton " +
                "ON organizations (is_system) WHERE is_system = true;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_organizations_is_system_singleton;");
            migrationBuilder.RenameColumn(
                name: "is_system",
                table: "organizations",
                newName: "is_seeded");
            // Re-stamp the Default org as already seeded so a rolled-back boot
            // doesn't try to re-import on-disk seed content that the new code
            // path has stopped shipping.
            migrationBuilder.Sql(
                "UPDATE organizations SET is_seeded = true WHERE slug = 'default';");
        }
    }
}
