using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationUsageSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organization_usage_snapshots",
                columns: table => new
                {
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    logical_bytes = table.Column<long>(type: "bigint", nullable: false),
                    index_bytes = table.Column<long>(type: "bigint", nullable: false),
                    computed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_usage_snapshots", x => x.organization_id);
                    table.ForeignKey(
                        name: "FK_organization_usage_snapshots_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "organization_usage_snapshots");
        }
    }
}
