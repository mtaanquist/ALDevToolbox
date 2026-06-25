using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class MakeCustomerNameIndexCaseInsensitive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop the case-sensitive (organization_id, name) unique index and
            // replace it with a functional (organization_id, lower(name)) one so
            // uniqueness is case-insensitive, matching CustomerService's
            // case-insensitive pre-check. EF can't express a functional index, so
            // it's created via raw SQL (issue #432). The plain FK-backing index
            // below replaces the column coverage the dropped composite provided.
            migrationBuilder.DropIndex(
                name: "ix_oe_customers_org_name_active",
                table: "oe_customers");

            migrationBuilder.CreateIndex(
                name: "IX_oe_customers_organization_id",
                table: "oe_customers",
                column: "organization_id");

            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_oe_customers_org_name_active " +
                "ON oe_customers (organization_id, lower(name)) " +
                "WHERE deleted_at IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_oe_customers_org_name_active;");

            migrationBuilder.DropIndex(
                name: "IX_oe_customers_organization_id",
                table: "oe_customers");

            migrationBuilder.CreateIndex(
                name: "ix_oe_customers_org_name_active",
                table: "oe_customers",
                columns: new[] { "organization_id", "name" },
                unique: true,
                filter: "\"deleted_at\" IS NULL");
        }
    }
}
