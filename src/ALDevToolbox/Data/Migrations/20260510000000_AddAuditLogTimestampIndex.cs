using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Adds a standalone <c>(timestamp)</c> index on <c>audit_log</c>. The
    /// existing compound index <c>(entity_type, entity_id, timestamp)</c>
    /// covers per-entity history queries; the global <c>/admin/audit</c>
    /// overview orders by timestamp with no entity filter and was doing a
    /// full sort.
    /// </summary>
    public partial class AddAuditLogTimestampIndex : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_audit_log_timestamp",
                table: "audit_log",
                column: "timestamp");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audit_log_timestamp",
                table: "audit_log");
        }
    }
}
