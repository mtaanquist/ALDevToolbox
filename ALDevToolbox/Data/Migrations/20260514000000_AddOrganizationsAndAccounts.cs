using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Milestone P3.13 — replaces the single shared admin password with
    /// organisation-scoped accounts. Adds the <c>organizations</c>, <c>users</c>,
    /// <c>signup_requests</c>, <c>password_reset_tokens</c> and
    /// <c>login_attempts</c> tables, then stamps every previously-global row
    /// (<c>runtime_templates</c>, <c>modules</c>, <c>well_known_dependencies</c>,
    /// <c>application_versions</c>, the template / module folders &amp; files,
    /// <c>runtime_template_default_modules</c>, <c>module_dependencies</c>) with
    /// an <c>organization_id</c> pointing at the seeded "Default" organisation
    /// (id = 1). Per-org uniqueness replaces global uniqueness on the
    /// <c>key</c> indexes. <c>audit_log</c> picks up nullable
    /// <c>organization_id</c> and <c>changed_by_user_id</c> columns plus an
    /// org-scoped timestamp index.
    ///
    /// Migration steps:
    /// <list type="number">
    ///   <item>Create <c>organizations</c>; insert the Default org (id = 1)
    ///         marked as already seeded so first-run code skips it.</item>
    ///   <item>Create the rest of the auth tables.</item>
    ///   <item>Add <c>organization_id</c> NOT NULL columns to every editable
    ///         table, defaulted to 1 so existing rows backfill automatically.
    ///         The default is kept on the column post-migration; service code
    ///         always supplies the value, so nothing relies on it.</item>
    ///   <item>Drop the global-unique <c>key</c> indexes; recreate as
    ///         <c>(organization_id, key)</c> composite uniques.</item>
    ///   <item>Add the audit-log <c>organization_id</c> and
    ///         <c>changed_by_user_id</c> columns (nullable) and the per-org
    ///         timestamp index.</item>
    /// </list>
    /// </summary>
    public partial class AddOrganizationsAndAccounts : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. organizations table + Default row.
            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    slug = table.Column<string>(type: "TEXT", nullable: false),
                    is_pending = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_seeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<System.DateTime>(type: "TEXT", nullable: false),
                },
                constraints: table => table.PrimaryKey("PK_organizations", x => x.id));

            migrationBuilder.CreateIndex(
                name: "IX_organizations_slug",
                table: "organizations",
                column: "slug",
                unique: true);

            // The Default organisation owns every row that existed before this
            // migration. is_seeded = true so SeedService doesn't run against it
            // again at next startup (the existing data already populated it).
            migrationBuilder.Sql(
                "INSERT INTO organizations (id, name, slug, is_pending, is_seeded, created_at) "
                + "VALUES (1, 'Default', 'default', 0, 1, '" + System.DateTime.UtcNow.ToString("o") + "');");

            // 2. users / signup_requests / password_reset_tokens / login_attempts.
            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    organization_id = table.Column<int>(type: "INTEGER", nullable: false),
                    email = table.Column<string>(type: "TEXT", nullable: false),
                    password_hash = table.Column<string>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", nullable: false),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<System.DateTime>(type: "TEXT", nullable: false),
                    last_login_at = table.Column<System.DateTime>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                    table.ForeignKey(
                        name: "FK_users_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_users_organization_id_email",
                table: "users",
                columns: new[] { "organization_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_email",
                table: "users",
                column: "email");

            migrationBuilder.CreateTable(
                name: "signup_requests",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    organization_id = table.Column<int>(type: "INTEGER", nullable: false),
                    user_id = table.Column<int>(type: "INTEGER", nullable: true),
                    email = table.Column<string>(type: "TEXT", nullable: false),
                    requested_at = table.Column<System.DateTime>(type: "TEXT", nullable: false),
                    decided_at = table.Column<System.DateTime>(type: "TEXT", nullable: true),
                    decided_by_user_id = table.Column<int>(type: "INTEGER", nullable: true),
                    decision = table.Column<string>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signup_requests", x => x.id);
                    table.ForeignKey(
                        name: "FK_signup_requests_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_signup_requests_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_signup_requests_organization_id_decision",
                table: "signup_requests",
                columns: new[] { "organization_id", "decision" });

            migrationBuilder.CreateIndex(
                name: "IX_signup_requests_user_id",
                table: "signup_requests",
                column: "user_id");

            migrationBuilder.CreateTable(
                name: "password_reset_tokens",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<int>(type: "INTEGER", nullable: false),
                    token_hash = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<System.DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<System.DateTime>(type: "TEXT", nullable: false),
                    consumed_at = table.Column<System.DateTime>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_password_reset_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_password_reset_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_tokens_token_hash",
                table: "password_reset_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_tokens_user_id",
                table: "password_reset_tokens",
                column: "user_id");

            migrationBuilder.CreateTable(
                name: "login_attempts",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    email = table.Column<string>(type: "TEXT", nullable: false),
                    ip = table.Column<string>(type: "TEXT", nullable: false),
                    succeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    timestamp = table.Column<System.DateTime>(type: "TEXT", nullable: false),
                },
                constraints: table => table.PrimaryKey("PK_login_attempts", x => x.id));

            migrationBuilder.CreateIndex(
                name: "IX_login_attempts_email_timestamp",
                table: "login_attempts",
                columns: new[] { "email", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_login_attempts_ip_timestamp",
                table: "login_attempts",
                columns: new[] { "ip", "timestamp" });

            // 3. organization_id columns on existing tables. defaultValue = 1
            //    backfills every pre-existing row to the Default org.
            AddOrganizationColumn(migrationBuilder, "runtime_templates");
            AddOrganizationColumn(migrationBuilder, "modules");
            AddOrganizationColumn(migrationBuilder, "well_known_dependencies");
            AddOrganizationColumn(migrationBuilder, "application_versions");
            AddOrganizationColumn(migrationBuilder, "template_folders");
            AddOrganizationColumn(migrationBuilder, "template_files");
            AddOrganizationColumn(migrationBuilder, "template_module_folders");
            AddOrganizationColumn(migrationBuilder, "template_module_files");
            AddOrganizationColumn(migrationBuilder, "runtime_template_default_modules");
            AddOrganizationColumn(migrationBuilder, "module_dependencies");

            // 4. Replace global-unique key indexes with per-org composites.
            migrationBuilder.DropIndex("IX_runtime_templates_key", "runtime_templates");
            migrationBuilder.CreateIndex(
                name: "IX_runtime_templates_organization_id_key",
                table: "runtime_templates",
                columns: new[] { "organization_id", "key" },
                unique: true);

            migrationBuilder.DropIndex("IX_modules_key", "modules");
            migrationBuilder.CreateIndex(
                name: "IX_modules_organization_id_key",
                table: "modules",
                columns: new[] { "organization_id", "key" },
                unique: true);

            migrationBuilder.DropIndex("IX_application_versions_key", "application_versions");
            migrationBuilder.CreateIndex(
                name: "IX_application_versions_organization_id_key",
                table: "application_versions",
                columns: new[] { "organization_id", "key" },
                unique: true);

            // Per-org indexes for the rest. The pre-existing single-column
            // indexes (template_id, ordering) etc. stay in place; the new
            // composites are denormalised support so org-scoped queries can
            // hit a single index.
            migrationBuilder.CreateIndex(
                name: "IX_template_folders_organization_id_template_id_ordering",
                table: "template_folders",
                columns: new[] { "organization_id", "template_id", "ordering" });
            migrationBuilder.CreateIndex(
                name: "IX_template_files_organization_id_template_folder_id_ordering",
                table: "template_files",
                columns: new[] { "organization_id", "template_folder_id", "ordering" });
            migrationBuilder.CreateIndex(
                name: "IX_template_module_folders_organization_id_template_id_ordering",
                table: "template_module_folders",
                columns: new[] { "organization_id", "template_id", "ordering" });
            migrationBuilder.CreateIndex(
                name: "IX_template_module_files_organization_id_template_module_folder_id_ordering",
                table: "template_module_files",
                columns: new[] { "organization_id", "template_module_folder_id", "ordering" });
            migrationBuilder.CreateIndex(
                name: "IX_runtime_template_default_modules_organization_id_runtime_template_id_ordering",
                table: "runtime_template_default_modules",
                columns: new[] { "organization_id", "runtime_template_id", "ordering" });
            migrationBuilder.CreateIndex(
                name: "IX_module_dependencies_organization_id_module_id_ordering",
                table: "module_dependencies",
                columns: new[] { "organization_id", "module_id", "ordering" });
            migrationBuilder.CreateIndex(
                name: "IX_well_known_dependencies_organization_id_ordering",
                table: "well_known_dependencies",
                columns: new[] { "organization_id", "ordering" });

            // Foreign keys to organizations on the principal tables. SQLite
            // rebuilds the table to add an FK; child tables inherit org via
            // their parent's cascade so we skip FKs there.
            migrationBuilder.AddForeignKey(
                name: "FK_runtime_templates_organizations_organization_id",
                table: "runtime_templates",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(
                name: "FK_modules_organizations_organization_id",
                table: "modules",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(
                name: "FK_well_known_dependencies_organizations_organization_id",
                table: "well_known_dependencies",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(
                name: "FK_application_versions_organizations_organization_id",
                table: "application_versions",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            // 5. audit_log gains nullable organization_id + changed_by_user_id
            //    plus an org-scoped timestamp index for /admin/audit.
            migrationBuilder.AddColumn<int>(
                name: "organization_id",
                table: "audit_log",
                type: "INTEGER",
                nullable: true);
            migrationBuilder.AddColumn<int>(
                name: "changed_by_user_id",
                table: "audit_log",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_org_timestamp",
                table: "audit_log",
                columns: new[] { "organization_id", "timestamp" });

            // Existing audit rows belong to the Default org by construction —
            // they were written before multi-tenancy existed.
            migrationBuilder.Sql("UPDATE audit_log SET organization_id = 1 WHERE organization_id IS NULL;");

            migrationBuilder.AddForeignKey(
                name: "FK_audit_log_organizations_organization_id",
                table: "audit_log",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
            migrationBuilder.AddForeignKey(
                name: "FK_audit_log_users_changed_by_user_id",
                table: "audit_log",
                column: "changed_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        private static void AddOrganizationColumn(MigrationBuilder migrationBuilder, string table)
        {
            migrationBuilder.AddColumn<int>(
                name: "organization_id",
                table: table,
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Audit-log columns + indexes / FKs.
            migrationBuilder.DropForeignKey("FK_audit_log_organizations_organization_id", "audit_log");
            migrationBuilder.DropForeignKey("FK_audit_log_users_changed_by_user_id", "audit_log");
            migrationBuilder.DropIndex("ix_audit_log_org_timestamp", "audit_log");
            migrationBuilder.DropColumn("organization_id", "audit_log");
            migrationBuilder.DropColumn("changed_by_user_id", "audit_log");

            // Restore global-unique key indexes.
            migrationBuilder.DropIndex("IX_runtime_templates_organization_id_key", "runtime_templates");
            migrationBuilder.DropIndex("IX_modules_organization_id_key", "modules");
            migrationBuilder.DropIndex("IX_application_versions_organization_id_key", "application_versions");
            migrationBuilder.DropIndex("IX_template_folders_organization_id_template_id_ordering", "template_folders");
            migrationBuilder.DropIndex("IX_template_files_organization_id_template_folder_id_ordering", "template_files");
            migrationBuilder.DropIndex("IX_template_module_folders_organization_id_template_id_ordering", "template_module_folders");
            migrationBuilder.DropIndex("IX_template_module_files_organization_id_template_module_folder_id_ordering", "template_module_files");
            migrationBuilder.DropIndex("IX_runtime_template_default_modules_organization_id_runtime_template_id_ordering", "runtime_template_default_modules");
            migrationBuilder.DropIndex("IX_module_dependencies_organization_id_module_id_ordering", "module_dependencies");
            migrationBuilder.DropIndex("IX_well_known_dependencies_organization_id_ordering", "well_known_dependencies");

            migrationBuilder.CreateIndex("IX_runtime_templates_key", "runtime_templates", "key", unique: true);
            migrationBuilder.CreateIndex("IX_modules_key", "modules", "key", unique: true);
            migrationBuilder.CreateIndex("IX_application_versions_key", "application_versions", "key", unique: true);

            // Drop FK + organization_id columns.
            migrationBuilder.DropForeignKey("FK_runtime_templates_organizations_organization_id", "runtime_templates");
            migrationBuilder.DropForeignKey("FK_modules_organizations_organization_id", "modules");
            migrationBuilder.DropForeignKey("FK_well_known_dependencies_organizations_organization_id", "well_known_dependencies");
            migrationBuilder.DropForeignKey("FK_application_versions_organizations_organization_id", "application_versions");

            migrationBuilder.DropColumn("organization_id", "runtime_templates");
            migrationBuilder.DropColumn("organization_id", "modules");
            migrationBuilder.DropColumn("organization_id", "well_known_dependencies");
            migrationBuilder.DropColumn("organization_id", "application_versions");
            migrationBuilder.DropColumn("organization_id", "template_folders");
            migrationBuilder.DropColumn("organization_id", "template_files");
            migrationBuilder.DropColumn("organization_id", "template_module_folders");
            migrationBuilder.DropColumn("organization_id", "template_module_files");
            migrationBuilder.DropColumn("organization_id", "runtime_template_default_modules");
            migrationBuilder.DropColumn("organization_id", "module_dependencies");

            // Drop new tables.
            migrationBuilder.DropTable("login_attempts");
            migrationBuilder.DropTable("password_reset_tokens");
            migrationBuilder.DropTable("signup_requests");
            migrationBuilder.DropTable("users");
            migrationBuilder.DropTable("organizations");
        }
    }
}
