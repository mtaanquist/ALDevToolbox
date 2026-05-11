using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Adds the Snippets tool: a per-org library of reusable AL patterns plus a
    /// user-driven suggestion queue that admins approve or reject. End users
    /// view snippets in the browser; admins curate them and promote suggestions.
    ///
    /// Tables: <c>snippets</c>, <c>snippet_files</c>, <c>snippet_suggestions</c>,
    /// <c>snippet_suggestion_files</c>. The <c>pg_trgm</c> extension is enabled
    /// and a GIN trigram index over (title, description, keywords) on
    /// <c>snippets</c> backs the fuzzy ILIKE search on <c>/snippets</c>.
    /// </summary>
    public partial class AddSnippets : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // pg_trgm powers the GIN trigram index used by the fuzzy search.
            // CREATE EXTENSION is idempotent; safe to re-run on databases
            // where pg_trgm was already installed by another tool.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            migrationBuilder.CreateTable(
                name: "snippets",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    keywords = table.Column<string>(type: "text", nullable: false),
                    deprecated = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_snippets", x => x.id);
                    table.ForeignKey(
                        name: "FK_snippets_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_snippets_organization_id_title",
                table: "snippets",
                columns: new[] { "organization_id", "title" },
                unique: true);

            migrationBuilder.Sql(
                "CREATE INDEX ix_snippets_search_trgm ON snippets " +
                "USING GIN (title gin_trgm_ops, description gin_trgm_ops, keywords gin_trgm_ops);");

            migrationBuilder.CreateTable(
                name: "snippet_files",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    snippet_id = table.Column<int>(type: "integer", nullable: false),
                    ordering = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_snippet_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_snippet_files_snippets_snippet_id",
                        column: x => x.snippet_id,
                        principalTable: "snippets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_snippet_files_organization_id_snippet_id_ordering",
                table: "snippet_files",
                columns: new[] { "organization_id", "snippet_id", "ordering" });

            migrationBuilder.CreateTable(
                name: "snippet_suggestions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    suggested_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    keywords = table.Column<string>(type: "text", nullable: false),
                    decision = table.Column<string>(type: "text", nullable: false),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decided_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    decision_note = table.Column<string>(type: "text", nullable: true),
                    approved_snippet_id = table.Column<int>(type: "integer", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_snippet_suggestions", x => x.id);
                    table.ForeignKey(
                        name: "FK_snippet_suggestions_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_snippet_suggestions_users_suggested_by_user_id",
                        column: x => x.suggested_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_snippet_suggestions_users_decided_by_user_id",
                        column: x => x.decided_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_snippet_suggestions_snippets_approved_snippet_id",
                        column: x => x.approved_snippet_id,
                        principalTable: "snippets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_snippet_suggestions_organization_id_decision",
                table: "snippet_suggestions",
                columns: new[] { "organization_id", "decision" });

            migrationBuilder.CreateIndex(
                name: "IX_snippet_suggestions_suggested_by_user_id",
                table: "snippet_suggestions",
                column: "suggested_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_snippet_suggestions_decided_by_user_id",
                table: "snippet_suggestions",
                column: "decided_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_snippet_suggestions_approved_snippet_id",
                table: "snippet_suggestions",
                column: "approved_snippet_id");

            migrationBuilder.CreateTable(
                name: "snippet_suggestion_files",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    snippet_suggestion_id = table.Column<int>(type: "integer", nullable: false),
                    ordering = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_snippet_suggestion_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_snippet_suggestion_files_snippet_suggestions_snippet_suggestion_id",
                        column: x => x.snippet_suggestion_id,
                        principalTable: "snippet_suggestions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_snippet_suggestion_files_organization_id_snippet_suggestion_id_ordering",
                table: "snippet_suggestion_files",
                columns: new[] { "organization_id", "snippet_suggestion_id", "ordering" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("snippet_suggestion_files");
            migrationBuilder.DropTable("snippet_suggestions");
            migrationBuilder.DropTable("snippet_files");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_snippets_search_trgm;");
            migrationBuilder.DropTable("snippets");
            // Leave pg_trgm enabled — other migrations or installations may rely on it.
        }
    }
}
