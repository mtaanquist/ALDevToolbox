using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBcQualityKnowledgeBase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bcquality_articles",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    article_key = table.Column<string>(type: "text", nullable: false),
                    layer = table.Column<string>(type: "text", nullable: false),
                    domain = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    keywords = table.Column<List<string>>(type: "text[]", nullable: false),
                    keywords_text = table.Column<string>(type: "text", nullable: false),
                    technologies = table.Column<List<string>>(type: "text[]", nullable: false),
                    countries = table.Column<List<string>>(type: "text[]", nullable: false),
                    application_areas = table.Column<List<string>>(type: "text[]", nullable: false),
                    bc_version_raw = table.Column<string>(type: "text", nullable: false),
                    bc_version_all = table.Column<bool>(type: "boolean", nullable: false),
                    bc_versions = table.Column<List<int>>(type: "integer[]", nullable: false),
                    bc_version_from = table.Column<int>(type: "integer", nullable: true),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "setweight(to_tsvector('english', coalesce(title, '')), 'A') ||\nsetweight(to_tsvector('english', coalesce(keywords_text, '') || ' ' || coalesce(domain, '')), 'B') ||\nsetweight(to_tsvector('english', coalesce(summary, '')), 'C') ||\nsetweight(to_tsvector('english', coalesce(content, '')), 'D')", stored: true),
                    first_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bcquality_articles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bcquality_ingest_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    commit_sha = table.Column<string>(type: "text", nullable: false),
                    commit_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_success_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    article_count = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bcquality_ingest_state", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bcquality_article_samples",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    article_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    language = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bcquality_article_samples", x => x.id);
                    table.ForeignKey(
                        name: "FK_bcquality_article_samples_bcquality_articles_article_id",
                        column: x => x.article_id,
                        principalTable: "bcquality_articles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_bcquality_article_samples_file",
                table: "bcquality_article_samples",
                columns: new[] { "article_id", "file_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_bcquality_articles_domain",
                table: "bcquality_articles",
                column: "domain");

            migrationBuilder.CreateIndex(
                name: "ix_bcquality_articles_search",
                table: "bcquality_articles",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ux_bcquality_articles_key",
                table: "bcquality_articles",
                column: "article_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bcquality_article_samples");

            migrationBuilder.DropTable(
                name: "bcquality_ingest_state");

            migrationBuilder.DropTable(
                name: "bcquality_articles");
        }
    }
}
