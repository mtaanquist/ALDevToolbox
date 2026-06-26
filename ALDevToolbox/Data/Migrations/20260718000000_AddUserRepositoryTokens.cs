using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserRepositoryTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "azure_devops_pat_encrypted",
                table: "organization_settings");

            migrationBuilder.DropColumn(
                name: "github_pat_encrypted",
                table: "organization_settings");

            migrationBuilder.AddColumn<List<string>>(
                name: "allowed_repository_providers",
                table: "organization_settings",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.CreateTable(
                name: "user_repository_tokens",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    token_encrypted = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_repository_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_repository_tokens_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_repository_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_repository_tokens_organization_id",
                table: "user_repository_tokens",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_repository_tokens_user_id_organization_id_provider",
                table: "user_repository_tokens",
                columns: new[] { "user_id", "organization_id", "provider" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_repository_tokens");

            migrationBuilder.DropColumn(
                name: "allowed_repository_providers",
                table: "organization_settings");

            migrationBuilder.AddColumn<string>(
                name: "azure_devops_pat_encrypted",
                table: "organization_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "github_pat_encrypted",
                table: "organization_settings",
                type: "text",
                nullable: true);
        }
    }
}
