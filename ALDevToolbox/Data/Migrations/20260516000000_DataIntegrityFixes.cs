using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Issue #74: tighten data-layer integrity.
    /// <list type="bullet">
    /// <item>Audit-log FKs switch from <c>SET NULL</c> to <c>RESTRICT</c> so an
    /// organisation or user delete can't blank the audit chain.</item>
    /// <item>Add a filtered unique index on <c>signup_requests(organization_id,
    /// email)</c> for pending rows — closes a service-level race where two
    /// concurrent signups could pass the duplicate check.</item>
    /// <item>Add the missing explicit FK from
    /// <c>runtime_template_default_modules.organization_id</c> to
    /// <c>organizations.id</c>; without it EF inferred a shadow relationship
    /// with default behaviour that drifted from the rest of the tenant
    /// entities.</item>
    /// </list>
    /// </summary>
    public partial class DataIntegrityFixes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_audit_log_organizations_organization_id",
                table: "audit_log");
            migrationBuilder.AddForeignKey(
                name: "FK_audit_log_organizations_organization_id",
                table: "audit_log",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropForeignKey(
                name: "FK_audit_log_users_changed_by_user_id",
                table: "audit_log");
            migrationBuilder.AddForeignKey(
                name: "FK_audit_log_users_changed_by_user_id",
                table: "audit_log",
                column: "changed_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.CreateIndex(
                name: "ux_signup_requests_org_email_pending",
                table: "signup_requests",
                columns: new[] { "organization_id", "email" },
                unique: true,
                filter: "decision = 'Pending'");

            migrationBuilder.AddForeignKey(
                name: "FK_runtime_template_default_modules_organizations_organization_id",
                table: "runtime_template_default_modules",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_runtime_template_default_modules_organizations_organization_id",
                table: "runtime_template_default_modules");

            migrationBuilder.DropIndex(
                name: "ux_signup_requests_org_email_pending",
                table: "signup_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_audit_log_users_changed_by_user_id",
                table: "audit_log");
            migrationBuilder.AddForeignKey(
                name: "FK_audit_log_users_changed_by_user_id",
                table: "audit_log",
                column: "changed_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.DropForeignKey(
                name: "FK_audit_log_organizations_organization_id",
                table: "audit_log");
            migrationBuilder.AddForeignKey(
                name: "FK_audit_log_organizations_organization_id",
                table: "audit_log",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
