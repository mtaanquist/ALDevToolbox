using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameCustomerToProject : Migration
    {
        // Renames the Object Explorer "customer build" domain to "project". Pure
        // renames (ALTER ... RENAME) so existing rows survive — no table is
        // dropped or recreated. Tables, the FK columns, every PK/FK constraint and
        // index are renamed to the names EF now expects, the raw-SQL
        // case-insensitive name index is rebuilt under the new name, and the
        // stored Release/ImportJob kind discriminators are flipped in place.
        // See .design/object-explorer-project-builds.md.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The case-insensitive unique name index was created outside EF (raw
            // SQL, migration 20260714000000); drop it before the table rename and
            // recreate it on the new table at the end.
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_oe_customers_org_name_active;");

            // Columns on tables that are NOT renamed.
            migrationBuilder.RenameColumn(name: "customer_name", table: "oe_releases", newName: "project_name");
            migrationBuilder.RenameColumn(name: "customer_id", table: "oe_import_jobs", newName: "project_id");

            // Tables.
            migrationBuilder.RenameTable(name: "oe_customers", newName: "oe_projects");
            migrationBuilder.RenameTable(name: "oe_customer_repositories", newName: "oe_project_repositories");
            migrationBuilder.RenameTable(name: "oe_customer_symbols", newName: "oe_project_symbols");
            migrationBuilder.RenameTable(name: "oe_customer_build_results", newName: "oe_project_build_results");

            // FK columns on the renamed child tables.
            migrationBuilder.RenameColumn(name: "customer_id", table: "oe_project_repositories", newName: "project_id");
            migrationBuilder.RenameColumn(name: "customer_id", table: "oe_project_symbols", newName: "project_id");

            // Primary keys (RENAME CONSTRAINT also renames the backing index).
            migrationBuilder.Sql("ALTER TABLE oe_projects RENAME CONSTRAINT \"PK_oe_customers\" TO \"PK_oe_projects\";");
            migrationBuilder.Sql("ALTER TABLE oe_project_repositories RENAME CONSTRAINT \"PK_oe_customer_repositories\" TO \"PK_oe_project_repositories\";");
            migrationBuilder.Sql("ALTER TABLE oe_project_symbols RENAME CONSTRAINT \"PK_oe_customer_symbols\" TO \"PK_oe_project_symbols\";");
            migrationBuilder.Sql("ALTER TABLE oe_project_build_results RENAME CONSTRAINT \"PK_oe_customer_build_results\" TO \"PK_oe_project_build_results\";");

            // Foreign keys.
            migrationBuilder.Sql("ALTER TABLE oe_projects RENAME CONSTRAINT \"FK_oe_customers_organizations_organization_id\" TO \"FK_oe_projects_organizations_organization_id\";");
            migrationBuilder.Sql("ALTER TABLE oe_project_repositories RENAME CONSTRAINT \"FK_oe_customer_repositories_oe_customers_customer_id\" TO \"FK_oe_project_repositories_oe_projects_project_id\";");
            migrationBuilder.Sql("ALTER TABLE oe_project_repositories RENAME CONSTRAINT \"FK_oe_customer_repositories_organizations_organization_id\" TO \"FK_oe_project_repositories_organizations_organization_id\";");
            migrationBuilder.Sql("ALTER TABLE oe_project_symbols RENAME CONSTRAINT \"FK_oe_customer_symbols_oe_customers_customer_id\" TO \"FK_oe_project_symbols_oe_projects_project_id\";");
            migrationBuilder.Sql("ALTER TABLE oe_project_symbols RENAME CONSTRAINT \"FK_oe_customer_symbols_organizations_organization_id\" TO \"FK_oe_project_symbols_organizations_organization_id\";");
            migrationBuilder.Sql("ALTER TABLE oe_project_build_results RENAME CONSTRAINT \"FK_oe_customer_build_results_oe_releases_release_id\" TO \"FK_oe_project_build_results_oe_releases_release_id\";");
            migrationBuilder.Sql("ALTER TABLE oe_project_build_results RENAME CONSTRAINT \"FK_oe_customer_build_results_organizations_organization_id\" TO \"FK_oe_project_build_results_organizations_organization_id\";");

            // EF-managed indexes.
            migrationBuilder.RenameIndex(name: "IX_oe_customers_organization_id", newName: "IX_oe_projects_organization_id", table: "oe_projects");
            migrationBuilder.RenameIndex(name: "IX_oe_customer_repositories_organization_id", newName: "IX_oe_project_repositories_organization_id", table: "oe_project_repositories");
            migrationBuilder.RenameIndex(name: "ix_oe_customer_repositories_customer", newName: "ix_oe_project_repositories_project", table: "oe_project_repositories");
            migrationBuilder.RenameIndex(name: "IX_oe_customer_symbols_organization_id", newName: "IX_oe_project_symbols_organization_id", table: "oe_project_symbols");
            migrationBuilder.RenameIndex(name: "ix_oe_customer_symbols_customer_file", newName: "ix_oe_project_symbols_project_file", table: "oe_project_symbols");
            migrationBuilder.RenameIndex(name: "IX_oe_customer_build_results_organization_id", newName: "IX_oe_project_build_results_organization_id", table: "oe_project_build_results");
            migrationBuilder.RenameIndex(name: "ix_oe_customer_build_results_release", newName: "ix_oe_project_build_results_release", table: "oe_project_build_results");

            // Recreate the raw-SQL case-insensitive unique name index on the new table.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_oe_projects_org_name_active " +
                "ON oe_projects (organization_id, lower(name)) " +
                "WHERE deleted_at IS NULL;");

            // Flip the stored kind discriminators in place.
            migrationBuilder.Sql("UPDATE oe_releases SET kind = 'project' WHERE kind = 'customer';");
            migrationBuilder.Sql("UPDATE oe_import_jobs SET kind = 'project_build' WHERE kind = 'customer_build';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE oe_import_jobs SET kind = 'customer_build' WHERE kind = 'project_build';");
            migrationBuilder.Sql("UPDATE oe_releases SET kind = 'customer' WHERE kind = 'project';");

            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_oe_projects_org_name_active;");

            migrationBuilder.RenameIndex(name: "ix_oe_project_build_results_release", newName: "ix_oe_customer_build_results_release", table: "oe_project_build_results");
            migrationBuilder.RenameIndex(name: "IX_oe_project_build_results_organization_id", newName: "IX_oe_customer_build_results_organization_id", table: "oe_project_build_results");
            migrationBuilder.RenameIndex(name: "ix_oe_project_symbols_project_file", newName: "ix_oe_customer_symbols_customer_file", table: "oe_project_symbols");
            migrationBuilder.RenameIndex(name: "IX_oe_project_symbols_organization_id", newName: "IX_oe_customer_symbols_organization_id", table: "oe_project_symbols");
            migrationBuilder.RenameIndex(name: "ix_oe_project_repositories_project", newName: "ix_oe_customer_repositories_customer", table: "oe_project_repositories");
            migrationBuilder.RenameIndex(name: "IX_oe_project_repositories_organization_id", newName: "IX_oe_customer_repositories_organization_id", table: "oe_project_repositories");
            migrationBuilder.RenameIndex(name: "IX_oe_projects_organization_id", newName: "IX_oe_customers_organization_id", table: "oe_projects");

            migrationBuilder.Sql("ALTER TABLE oe_project_build_results RENAME CONSTRAINT \"FK_oe_project_build_results_organizations_organization_id\" TO \"FK_oe_customer_build_results_organizations_organization_id\";");
            migrationBuilder.Sql("ALTER TABLE oe_project_build_results RENAME CONSTRAINT \"FK_oe_project_build_results_oe_releases_release_id\" TO \"FK_oe_customer_build_results_oe_releases_release_id\";");
            migrationBuilder.Sql("ALTER TABLE oe_project_symbols RENAME CONSTRAINT \"FK_oe_project_symbols_organizations_organization_id\" TO \"FK_oe_customer_symbols_organizations_organization_id\";");
            migrationBuilder.Sql("ALTER TABLE oe_project_symbols RENAME CONSTRAINT \"FK_oe_project_symbols_oe_projects_project_id\" TO \"FK_oe_customer_symbols_oe_customers_customer_id\";");
            migrationBuilder.Sql("ALTER TABLE oe_project_repositories RENAME CONSTRAINT \"FK_oe_project_repositories_organizations_organization_id\" TO \"FK_oe_customer_repositories_organizations_organization_id\";");
            migrationBuilder.Sql("ALTER TABLE oe_project_repositories RENAME CONSTRAINT \"FK_oe_project_repositories_oe_projects_project_id\" TO \"FK_oe_customer_repositories_oe_customers_customer_id\";");
            migrationBuilder.Sql("ALTER TABLE oe_projects RENAME CONSTRAINT \"FK_oe_projects_organizations_organization_id\" TO \"FK_oe_customers_organizations_organization_id\";");

            migrationBuilder.Sql("ALTER TABLE oe_project_build_results RENAME CONSTRAINT \"PK_oe_project_build_results\" TO \"PK_oe_customer_build_results\";");
            migrationBuilder.Sql("ALTER TABLE oe_project_symbols RENAME CONSTRAINT \"PK_oe_project_symbols\" TO \"PK_oe_customer_symbols\";");
            migrationBuilder.Sql("ALTER TABLE oe_project_repositories RENAME CONSTRAINT \"PK_oe_project_repositories\" TO \"PK_oe_customer_repositories\";");
            migrationBuilder.Sql("ALTER TABLE oe_projects RENAME CONSTRAINT \"PK_oe_projects\" TO \"PK_oe_customers\";");

            migrationBuilder.RenameColumn(name: "project_id", table: "oe_project_symbols", newName: "customer_id");
            migrationBuilder.RenameColumn(name: "project_id", table: "oe_project_repositories", newName: "customer_id");

            migrationBuilder.RenameTable(name: "oe_project_build_results", newName: "oe_customer_build_results");
            migrationBuilder.RenameTable(name: "oe_project_symbols", newName: "oe_customer_symbols");
            migrationBuilder.RenameTable(name: "oe_project_repositories", newName: "oe_customer_repositories");
            migrationBuilder.RenameTable(name: "oe_projects", newName: "oe_customers");

            migrationBuilder.RenameColumn(name: "project_id", table: "oe_import_jobs", newName: "customer_id");
            migrationBuilder.RenameColumn(name: "project_name", table: "oe_releases", newName: "customer_name");

            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_oe_customers_org_name_active " +
                "ON oe_customers (organization_id, lower(name)) " +
                "WHERE deleted_at IS NULL;");
        }
    }
}
