using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class PerTenantOffsiteBackups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "offsite_object_key",
                table: "per_tenant_backups",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "offsite_uploaded_at",
                table: "per_tenant_backups",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "offsite_object_key",
                table: "per_tenant_backups");

            migrationBuilder.DropColumn(
                name: "offsite_uploaded_at",
                table: "per_tenant_backups");
        }
    }
}
