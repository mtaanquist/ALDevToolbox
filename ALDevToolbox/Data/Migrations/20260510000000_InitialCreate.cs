using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Single Postgres-only baseline migration introduced in P4.16. See the
    /// "Migration path" section of <c>.design/milestones.md</c> for the
    /// upgrade story; audit-log history is intentionally not preserved.
    /// </summary>
    public partial class InitialCreate : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    is_pending = table.Column<bool>(type: "boolean", nullable: false),
                    is_seeded = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table => table.PrimaryKey("PK_organizations", x => x.id));

            migrationBuilder.CreateIndex("IX_organizations_slug", "organizations", "slug", unique: true);

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_login_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
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

            migrationBuilder.CreateIndex("IX_users_email", "users", "email");
            migrationBuilder.CreateIndex(
                "IX_users_organization_id_email",
                "users",
                new[] { "organization_id", "email" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "signup_requests",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<int>(type: "integer", nullable: true),
                    email = table.Column<string>(type: "text", nullable: false),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decided_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    decision = table.Column<string>(type: "text", nullable: false),
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
                "IX_signup_requests_organization_id_decision",
                "signup_requests",
                new[] { "organization_id", "decision" });
            migrationBuilder.CreateIndex("IX_signup_requests_user_id", "signup_requests", "user_id");

            migrationBuilder.CreateTable(
                name: "password_reset_tokens",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
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

            migrationBuilder.CreateIndex("IX_password_reset_tokens_token_hash", "password_reset_tokens", "token_hash", unique: true);
            migrationBuilder.CreateIndex("IX_password_reset_tokens_user_id", "password_reset_tokens", "user_id");

            migrationBuilder.CreateTable(
                name: "login_attempts",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    email = table.Column<string>(type: "text", nullable: false),
                    ip = table.Column<string>(type: "text", nullable: false),
                    succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table => table.PrimaryKey("PK_login_attempts", x => x.id));

            migrationBuilder.CreateIndex("IX_login_attempts_email_timestamp", "login_attempts", new[] { "email", "timestamp" });
            migrationBuilder.CreateIndex("IX_login_attempts_ip_timestamp", "login_attempts", new[] { "ip", "timestamp" });

            migrationBuilder.CreateTable(
                name: "application_versions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    application = table.Column<string>(type: "text", nullable: false),
                    runtime = table.Column<string>(type: "text", nullable: false),
                    ordering = table.Column<int>(type: "integer", nullable: false),
                    deprecated = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_versions", x => x.id);
                    table.ForeignKey(
                        name: "FK_application_versions_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                "IX_application_versions_organization_id_key",
                "application_versions",
                new[] { "organization_id", "key" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "modules",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    id_range_size = table.Column<int>(type: "integer", nullable: true),
                    deprecated = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_modules", x => x.id);
                    table.ForeignKey(
                        name: "FK_modules_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                "IX_modules_organization_id_key",
                "modules",
                new[] { "organization_id", "key" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "module_dependencies",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    module_id = table.Column<int>(type: "integer", nullable: false),
                    ordering = table.Column<int>(type: "integer", nullable: false),
                    dep_id = table.Column<string>(type: "text", nullable: false),
                    dep_name = table.Column<string>(type: "text", nullable: false),
                    dep_publisher = table.Column<string>(type: "text", nullable: false),
                    dep_version = table.Column<string>(type: "text", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_module_dependencies", x => x.id);
                    table.ForeignKey(
                        name: "FK_module_dependencies_modules_module_id",
                        column: x => x.module_id,
                        principalTable: "modules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                "IX_module_dependencies_organization_id_module_id_ordering",
                "module_dependencies",
                new[] { "organization_id", "module_id", "ordering" });

            migrationBuilder.CreateTable(
                name: "well_known_dependencies",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    dep_id = table.Column<string>(type: "text", nullable: false),
                    dep_name = table.Column<string>(type: "text", nullable: false),
                    dep_publisher = table.Column<string>(type: "text", nullable: false),
                    dep_version_default = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: true),
                    ordering = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_well_known_dependencies", x => x.id);
                    table.ForeignKey(
                        name: "FK_well_known_dependencies_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                "IX_well_known_dependencies_organization_id_ordering",
                "well_known_dependencies",
                new[] { "organization_id", "ordering" });

            migrationBuilder.CreateTable(
                name: "runtime_templates",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    runtime = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    default_application = table.Column<string>(type: "text", nullable: false),
                    default_platform = table.Column<string>(type: "text", nullable: false),
                    default_application_version_id = table.Column<int>(type: "integer", nullable: true),
                    defaults_json = table.Column<string>(type: "jsonb", nullable: false),
                    app_source_cop_json = table.Column<string>(type: "jsonb", nullable: false),
                    core_id_range_from = table.Column<int>(type: "integer", nullable: false),
                    core_id_range_to = table.Column<int>(type: "integer", nullable: false),
                    module_id_range_start = table.Column<int>(type: "integer", nullable: false),
                    module_id_range_size = table.Column<int>(type: "integer", nullable: false),
                    deprecated = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_templates", x => x.id);
                    table.ForeignKey(
                        name: "FK_runtime_templates_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_runtime_templates_application_versions_default_application_version_id",
                        column: x => x.default_application_version_id,
                        principalTable: "application_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                "IX_runtime_templates_organization_id_key",
                "runtime_templates",
                new[] { "organization_id", "key" },
                unique: true);
            migrationBuilder.CreateIndex(
                "IX_runtime_templates_default_application_version_id",
                "runtime_templates",
                "default_application_version_id");

            migrationBuilder.CreateTable(
                name: "template_folders",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    template_id = table.Column<int>(type: "integer", nullable: false),
                    ordering = table.Column<int>(type: "integer", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_template_folders", x => x.id);
                    table.ForeignKey(
                        name: "FK_template_folders_runtime_templates_template_id",
                        column: x => x.template_id,
                        principalTable: "runtime_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                "IX_template_folders_organization_id_template_id_ordering",
                "template_folders",
                new[] { "organization_id", "template_id", "ordering" });
            migrationBuilder.CreateIndex(
                "IX_template_folders_template_id",
                "template_folders",
                "template_id");

            migrationBuilder.CreateTable(
                name: "template_files",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    template_folder_id = table.Column<int>(type: "integer", nullable: false),
                    ordering = table.Column<int>(type: "integer", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_template_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_template_files_template_folders_template_folder_id",
                        column: x => x.template_folder_id,
                        principalTable: "template_folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                "IX_template_files_organization_id_template_folder_id_ordering",
                "template_files",
                new[] { "organization_id", "template_folder_id", "ordering" });
            migrationBuilder.CreateIndex(
                "IX_template_files_template_folder_id_path",
                "template_files",
                new[] { "template_folder_id", "path" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "template_module_folders",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    template_id = table.Column<int>(type: "integer", nullable: false),
                    ordering = table.Column<int>(type: "integer", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_template_module_folders", x => x.id);
                    table.ForeignKey(
                        name: "FK_template_module_folders_runtime_templates_template_id",
                        column: x => x.template_id,
                        principalTable: "runtime_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                "IX_template_module_folders_organization_id_template_id_ordering",
                "template_module_folders",
                new[] { "organization_id", "template_id", "ordering" });
            migrationBuilder.CreateIndex(
                "IX_template_module_folders_template_id",
                "template_module_folders",
                "template_id");

            migrationBuilder.CreateTable(
                name: "template_module_files",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    template_module_folder_id = table.Column<int>(type: "integer", nullable: false),
                    ordering = table.Column<int>(type: "integer", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_template_module_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_template_module_files_template_module_folders_template_module_folder_id",
                        column: x => x.template_module_folder_id,
                        principalTable: "template_module_folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                "IX_template_module_files_organization_id_template_module_folder_id_ordering",
                "template_module_files",
                new[] { "organization_id", "template_module_folder_id", "ordering" });
            migrationBuilder.CreateIndex(
                "IX_template_module_files_template_module_folder_id_path",
                "template_module_files",
                new[] { "template_module_folder_id", "path" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "runtime_template_default_modules",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    runtime_template_id = table.Column<int>(type: "integer", nullable: false),
                    module_id = table.Column<int>(type: "integer", nullable: false),
                    ordering = table.Column<int>(type: "integer", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_template_default_modules", x => x.id);
                    table.ForeignKey(
                        name: "FK_runtime_template_default_modules_runtime_templates_runtime_template_id",
                        column: x => x.runtime_template_id,
                        principalTable: "runtime_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_runtime_template_default_modules_modules_module_id",
                        column: x => x.module_id,
                        principalTable: "modules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                "IX_runtime_template_default_modules_organization_id_runtime_template_id_ordering",
                "runtime_template_default_modules",
                new[] { "organization_id", "runtime_template_id", "ordering" });
            migrationBuilder.CreateIndex(
                "IX_runtime_template_default_modules_runtime_template_id_module_id",
                "runtime_template_default_modules",
                new[] { "runtime_template_id", "module_id" },
                unique: true);
            migrationBuilder.CreateIndex(
                "IX_runtime_template_default_modules_module_id",
                "runtime_template_default_modules",
                "module_id");

            migrationBuilder.CreateTable(
                name: "organization_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    default_publisher = table.Column<string>(type: "text", nullable: false),
                    default_id_range_from = table.Column<int>(type: "integer", nullable: false),
                    default_id_range_to = table.Column<int>(type: "integer", nullable: false),
                    default_brief = table.Column<string>(type: "text", nullable: false),
                    default_core_description = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_settings", x => x.id);
                    table.ForeignKey(
                        name: "FK_organization_settings_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                "IX_organization_settings_organization_id",
                "organization_settings",
                "organization_id",
                unique: true);

            migrationBuilder.CreateTable(
                name: "organization_assets",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_assets", x => x.id);
                    table.ForeignKey(
                        name: "FK_organization_assets_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                "IX_organization_assets_organization_id_kind",
                "organization_assets",
                new[] { "organization_id", "kind" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "organization_files",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    mustache_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ordering = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_organization_files_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                "IX_organization_files_organization_id_ordering",
                "organization_files",
                new[] { "organization_id", "ordering" });
            migrationBuilder.CreateIndex(
                "IX_organization_files_organization_id_path",
                "organization_files",
                new[] { "organization_id", "path" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    changed_by = table.Column<string>(type: "text", nullable: false),
                    changed_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    organization_id = table.Column<int>(type: "integer", nullable: true),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<int>(type: "integer", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    snapshot_json = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.id);
                    table.ForeignKey(
                        name: "FK_audit_log_users_changed_by_user_id",
                        column: x => x.changed_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_audit_log_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                "ix_audit_log_entity_timestamp",
                "audit_log",
                new[] { "entity_type", "entity_id", "timestamp" });
            migrationBuilder.CreateIndex(
                "ix_audit_log_timestamp",
                "audit_log",
                "timestamp");
            migrationBuilder.CreateIndex(
                "ix_audit_log_org_timestamp",
                "audit_log",
                new[] { "organization_id", "timestamp" });
            migrationBuilder.CreateIndex(
                "IX_audit_log_changed_by_user_id",
                "audit_log",
                "changed_by_user_id");

            // Seed the Default organisation. The bootstrap pipeline in
            // Program.cs also lazily creates this row, but inserting it from
            // the migration keeps MigrateAsync sufficient on its own — the
            // smoke test in ALDevToolbox.Tests.Migrations relies on it.
            // pg_get_serial_sequence advances the identity sequence past the
            // explicit id so future inserts don't collide.
            migrationBuilder.Sql(@"
                INSERT INTO organizations (id, name, slug, is_pending, is_seeded, created_at)
                VALUES (1, 'Default', 'default', false, false, '2026-05-10T00:00:00Z' AT TIME ZONE 'UTC');
                SELECT setval(pg_get_serial_sequence('organizations', 'id'), 1, true);
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("audit_log");
            migrationBuilder.DropTable("organization_files");
            migrationBuilder.DropTable("organization_assets");
            migrationBuilder.DropTable("organization_settings");
            migrationBuilder.DropTable("runtime_template_default_modules");
            migrationBuilder.DropTable("template_module_files");
            migrationBuilder.DropTable("template_module_folders");
            migrationBuilder.DropTable("template_files");
            migrationBuilder.DropTable("template_folders");
            migrationBuilder.DropTable("runtime_templates");
            migrationBuilder.DropTable("module_dependencies");
            migrationBuilder.DropTable("modules");
            migrationBuilder.DropTable("well_known_dependencies");
            migrationBuilder.DropTable("application_versions");
            migrationBuilder.DropTable("login_attempts");
            migrationBuilder.DropTable("password_reset_tokens");
            migrationBuilder.DropTable("signup_requests");
            migrationBuilder.DropTable("users");
            migrationBuilder.DropTable("organizations");
        }
    }
}
