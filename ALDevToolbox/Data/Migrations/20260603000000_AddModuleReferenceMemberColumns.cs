using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Lays the schema groundwork for member-scoped Find references
    /// (procedure call sites, field accesses). Three new nullable
    /// columns on <c>oe_module_references</c>:
    /// <list type="bullet">
    ///   <item><c>target_member_name</c> — the procedure / field name
    ///         being referenced, when the row is member-scoped. Null
    ///         for the existing object-level reference kinds.</item>
    ///   <item><c>target_member_kind</c> — distinguishes a procedure
    ///         call from a same-named field access on the same owner.</item>
    ///   <item><c>target_symbol_id</c> — optional FK to the resolved
    ///         <c>oe_module_symbols</c> row inside the importing release.
    ///         Auxiliary; cross-release matching still goes through the
    ///         (target_app_id, kind, id/name, member_name) tuple.</item>
    /// </list>
    /// New partial index <c>ix_oe_module_references_target_member</c>
    /// supports the member-scoped lookups; filtered on rows where
    /// <c>target_member_name</c> is set so it stays empty until phase 2
    /// (method-call extraction) populates the column.
    /// </summary>
    public partial class AddModuleReferenceMemberColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "target_member_kind",
                table: "oe_module_references",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target_member_name",
                table: "oe_module_references",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "target_symbol_id",
                table: "oe_module_references",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_oe_module_references_target_member",
                table: "oe_module_references",
                columns: new[] { "target_app_id", "target_object_kind", "target_object_id", "target_member_name", "target_member_kind" },
                filter: "\"target_member_name\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_oe_module_references_target_symbol_id",
                table: "oe_module_references",
                column: "target_symbol_id");

            migrationBuilder.AddForeignKey(
                name: "FK_oe_module_references_oe_module_symbols_target_symbol_id",
                table: "oe_module_references",
                column: "target_symbol_id",
                principalTable: "oe_module_symbols",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oe_module_references_oe_module_symbols_target_symbol_id",
                table: "oe_module_references");

            migrationBuilder.DropIndex(
                name: "ix_oe_module_references_target_member",
                table: "oe_module_references");

            migrationBuilder.DropIndex(
                name: "IX_oe_module_references_target_symbol_id",
                table: "oe_module_references");

            migrationBuilder.DropColumn(
                name: "target_member_kind",
                table: "oe_module_references");

            migrationBuilder.DropColumn(
                name: "target_member_name",
                table: "oe_module_references");

            migrationBuilder.DropColumn(
                name: "target_symbol_id",
                table: "oe_module_references");
        }
    }
}
