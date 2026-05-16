using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class PerTenantBackups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "per_tenant_backup_retention_count",
                table: "system_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "per_tenant_backups",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    file_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    is_pinned = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_per_tenant_backups", x => x.id);
                    table.ForeignKey(
                        name: "FK_per_tenant_backups_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_per_tenant_backups_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_per_tenant_backups_created_by_user_id",
                table: "per_tenant_backups",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_per_tenant_backups_org_created",
                table: "per_tenant_backups",
                columns: new[] { "organization_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "per_tenant_backups");

            migrationBuilder.DropColumn(
                name: "per_tenant_backup_retention_count",
                table: "system_settings");
        }
    }
}
