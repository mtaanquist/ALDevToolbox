using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Renames the Snippets feature to Cookbook (entities renamed
    /// Snippet → Recipe, snippet_files → recipe_files, etc.), adds the
    /// Type discriminator (snippet / pattern / module) and the per-file
    /// RelativePath column for multi-folder layouts, and adds a
    /// cookbook_guidance markdown column on organization_settings that
    /// the new get_cookbook_guidance MCP tool reads.
    ///
    /// Schema preserves data: existing rows surface as Type = 0 (Snippet)
    /// with an empty RelativePath, identical to their pre-rename behaviour.
    ///
    /// Implementation note: index and constraint renames go through raw
    /// <c>ALTER ... RENAME</c> statements with the LONG identifier names
    /// from the original <c>AddSnippets</c> migration. Postgres truncates
    /// identifiers over 63 chars at parse time, so passing the long form
    /// matches whatever the original migration's identifier truncation
    /// produced — no need to guess at the stored truncated form. The
    /// rename operations never drop or recreate FKs, so the underlying
    /// constraints stay valid for the entire migration.
    /// </summary>
    public partial class RenameSnippetsToCookbook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- New columns --------------------------------------------------
            migrationBuilder.AddColumn<int>(
                name: "type",
                table: "snippets",
                type: "integer",
                nullable: false,
                defaultValue: 0);
            migrationBuilder.AddColumn<int>(
                name: "type",
                table: "snippet_suggestions",
                type: "integer",
                nullable: false,
                defaultValue: 0);
            migrationBuilder.AddColumn<string>(
                name: "relative_path",
                table: "snippet_files",
                type: "text",
                nullable: false,
                defaultValue: string.Empty);
            migrationBuilder.AddColumn<string>(
                name: "relative_path",
                table: "snippet_suggestion_files",
                type: "text",
                nullable: false,
                defaultValue: string.Empty);
            migrationBuilder.AddColumn<string>(
                name: "cookbook_guidance",
                table: "organization_settings",
                type: "text",
                nullable: false,
                defaultValue: string.Empty);

            // --- Table renames -----------------------------------------------
            migrationBuilder.RenameTable(name: "snippets", newName: "recipes");
            migrationBuilder.RenameTable(name: "snippet_files", newName: "recipe_files");
            migrationBuilder.RenameTable(name: "snippet_suggestions", newName: "recipe_suggestions");
            migrationBuilder.RenameTable(name: "snippet_suggestion_files", newName: "recipe_suggestion_files");

            // --- FK column renames -------------------------------------------
            migrationBuilder.RenameColumn(
                name: "snippet_id",
                table: "recipe_files",
                newName: "recipe_id");
            migrationBuilder.RenameColumn(
                name: "approved_snippet_id",
                table: "recipe_suggestions",
                newName: "approved_recipe_id");
            migrationBuilder.RenameColumn(
                name: "snippet_suggestion_id",
                table: "recipe_suggestion_files",
                newName: "recipe_suggestion_id");

            // --- Rename indexes and constraints in place. Postgres allows
            // ALTER INDEX ... RENAME TO and ALTER TABLE ... RENAME CONSTRAINT
            // without dropping/recreating, and silently truncates names
            // longer than 63 chars at parse time, so the long forms below
            // match whatever the original AddSnippets migration produced.
            migrationBuilder.Sql("""
                ALTER INDEX "IX_snippets_organization_id_title" RENAME TO "IX_recipes_organization_id_title";
                ALTER INDEX "IX_snippets_minimum_application_version_id" RENAME TO "IX_recipes_minimum_application_version_id";
                ALTER INDEX IF EXISTS "ix_snippets_search_trgm" RENAME TO "ix_recipes_search_trgm";
                ALTER INDEX "IX_snippet_files_organization_id_snippet_id_ordering" RENAME TO "IX_recipe_files_organization_id_recipe_id_ordering";
                ALTER INDEX "IX_snippet_suggestions_organization_id_decision" RENAME TO "IX_recipe_suggestions_organization_id_decision";
                ALTER INDEX "IX_snippet_suggestions_suggested_by_user_id" RENAME TO "IX_recipe_suggestions_suggested_by_user_id";
                ALTER INDEX "IX_snippet_suggestions_decided_by_user_id" RENAME TO "IX_recipe_suggestions_decided_by_user_id";
                ALTER INDEX "IX_snippet_suggestions_approved_snippet_id" RENAME TO "IX_recipe_suggestions_approved_recipe_id";
                ALTER INDEX "IX_snippet_suggestions_minimum_application_version_id" RENAME TO "IX_recipe_suggestions_minimum_application_version_id";
                ALTER INDEX "IX_snippet_suggestion_files_organization_id_snippet_suggestion_id_ordering" RENAME TO "IX_recipe_suggestion_files_organization_id_recipe_suggestion_id_ordering";

                ALTER TABLE recipes RENAME CONSTRAINT "PK_snippets" TO "PK_recipes";
                ALTER TABLE recipe_files RENAME CONSTRAINT "PK_snippet_files" TO "PK_recipe_files";
                ALTER TABLE recipe_suggestions RENAME CONSTRAINT "PK_snippet_suggestions" TO "PK_recipe_suggestions";
                ALTER TABLE recipe_suggestion_files RENAME CONSTRAINT "PK_snippet_suggestion_files" TO "PK_recipe_suggestion_files";

                ALTER TABLE recipes RENAME CONSTRAINT "FK_snippets_organizations_organization_id" TO "FK_recipes_organizations_organization_id";
                ALTER TABLE recipes RENAME CONSTRAINT "FK_snippets_application_versions_minimum_application_version_id" TO "FK_recipes_application_versions_minimum_application_version_id";
                ALTER TABLE recipe_files RENAME CONSTRAINT "FK_snippet_files_snippets_snippet_id" TO "FK_recipe_files_recipes_recipe_id";
                ALTER TABLE recipe_suggestions RENAME CONSTRAINT "FK_snippet_suggestions_organizations_organization_id" TO "FK_recipe_suggestions_organizations_organization_id";
                ALTER TABLE recipe_suggestions RENAME CONSTRAINT "FK_snippet_suggestions_users_suggested_by_user_id" TO "FK_recipe_suggestions_users_suggested_by_user_id";
                ALTER TABLE recipe_suggestions RENAME CONSTRAINT "FK_snippet_suggestions_users_decided_by_user_id" TO "FK_recipe_suggestions_users_decided_by_user_id";
                ALTER TABLE recipe_suggestions RENAME CONSTRAINT "FK_snippet_suggestions_snippets_approved_snippet_id" TO "FK_recipe_suggestions_recipes_approved_recipe_id";
                -- AddSnippetInstructionsAndMinimumApplicationVersion (20260616)
                -- emitted this FK pre-truncated with a `~` marker, so the
                -- stored name in Postgres ends with `~`; rename to a likewise
                -- pre-truncated target so the snapshot stays internally
                -- consistent with that migration's convention.
                ALTER TABLE recipe_suggestions RENAME CONSTRAINT "FK_snippet_suggestions_application_versions_minimum_applicatio~" TO "FK_recipe_suggestions_application_versions_minimum_application~";
                ALTER TABLE recipe_suggestion_files RENAME CONSTRAINT "FK_snippet_suggestion_files_snippet_suggestions_snippet_suggestion_id" TO "FK_recipe_suggestion_files_recipe_suggestions_recipe_suggestion_id";
                """);

            // --- New index for the type chip-row filter on /cookbook and
            // /admin/cookbook.
            migrationBuilder.CreateIndex(
                name: "IX_recipes_organization_id_type",
                table: "recipes",
                columns: new[] { "organization_id", "type" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_recipes_organization_id_type", table: "recipes");

            migrationBuilder.Sql("""
                ALTER TABLE recipe_suggestion_files RENAME CONSTRAINT "FK_recipe_suggestion_files_recipe_suggestions_recipe_suggestion_id" TO "FK_snippet_suggestion_files_snippet_suggestions_snippet_suggestion_id";
                ALTER TABLE recipe_suggestions RENAME CONSTRAINT "FK_recipe_suggestions_application_versions_minimum_application~" TO "FK_snippet_suggestions_application_versions_minimum_applicatio~";
                ALTER TABLE recipe_suggestions RENAME CONSTRAINT "FK_recipe_suggestions_recipes_approved_recipe_id" TO "FK_snippet_suggestions_snippets_approved_snippet_id";
                ALTER TABLE recipe_suggestions RENAME CONSTRAINT "FK_recipe_suggestions_users_decided_by_user_id" TO "FK_snippet_suggestions_users_decided_by_user_id";
                ALTER TABLE recipe_suggestions RENAME CONSTRAINT "FK_recipe_suggestions_users_suggested_by_user_id" TO "FK_snippet_suggestions_users_suggested_by_user_id";
                ALTER TABLE recipe_suggestions RENAME CONSTRAINT "FK_recipe_suggestions_organizations_organization_id" TO "FK_snippet_suggestions_organizations_organization_id";
                ALTER TABLE recipe_files RENAME CONSTRAINT "FK_recipe_files_recipes_recipe_id" TO "FK_snippet_files_snippets_snippet_id";
                ALTER TABLE recipes RENAME CONSTRAINT "FK_recipes_application_versions_minimum_application_version_id" TO "FK_snippets_application_versions_minimum_application_version_id";
                ALTER TABLE recipes RENAME CONSTRAINT "FK_recipes_organizations_organization_id" TO "FK_snippets_organizations_organization_id";

                ALTER TABLE recipe_suggestion_files RENAME CONSTRAINT "PK_recipe_suggestion_files" TO "PK_snippet_suggestion_files";
                ALTER TABLE recipe_suggestions RENAME CONSTRAINT "PK_recipe_suggestions" TO "PK_snippet_suggestions";
                ALTER TABLE recipe_files RENAME CONSTRAINT "PK_recipe_files" TO "PK_snippet_files";
                ALTER TABLE recipes RENAME CONSTRAINT "PK_recipes" TO "PK_snippets";

                ALTER INDEX "IX_recipe_suggestion_files_organization_id_recipe_suggestion_id_ordering" RENAME TO "IX_snippet_suggestion_files_organization_id_snippet_suggestion_id_ordering";
                ALTER INDEX "IX_recipe_suggestions_minimum_application_version_id" RENAME TO "IX_snippet_suggestions_minimum_application_version_id";
                ALTER INDEX "IX_recipe_suggestions_approved_recipe_id" RENAME TO "IX_snippet_suggestions_approved_snippet_id";
                ALTER INDEX "IX_recipe_suggestions_decided_by_user_id" RENAME TO "IX_snippet_suggestions_decided_by_user_id";
                ALTER INDEX "IX_recipe_suggestions_suggested_by_user_id" RENAME TO "IX_snippet_suggestions_suggested_by_user_id";
                ALTER INDEX "IX_recipe_suggestions_organization_id_decision" RENAME TO "IX_snippet_suggestions_organization_id_decision";
                ALTER INDEX "IX_recipe_files_organization_id_recipe_id_ordering" RENAME TO "IX_snippet_files_organization_id_snippet_id_ordering";
                ALTER INDEX IF EXISTS "ix_recipes_search_trgm" RENAME TO "ix_snippets_search_trgm";
                ALTER INDEX "IX_recipes_minimum_application_version_id" RENAME TO "IX_snippets_minimum_application_version_id";
                ALTER INDEX "IX_recipes_organization_id_title" RENAME TO "IX_snippets_organization_id_title";
                """);

            migrationBuilder.RenameColumn(
                name: "recipe_suggestion_id",
                table: "recipe_suggestion_files",
                newName: "snippet_suggestion_id");
            migrationBuilder.RenameColumn(
                name: "approved_recipe_id",
                table: "recipe_suggestions",
                newName: "approved_snippet_id");
            migrationBuilder.RenameColumn(
                name: "recipe_id",
                table: "recipe_files",
                newName: "snippet_id");

            migrationBuilder.RenameTable(name: "recipe_suggestion_files", newName: "snippet_suggestion_files");
            migrationBuilder.RenameTable(name: "recipe_suggestions", newName: "snippet_suggestions");
            migrationBuilder.RenameTable(name: "recipe_files", newName: "snippet_files");
            migrationBuilder.RenameTable(name: "recipes", newName: "snippets");

            migrationBuilder.DropColumn(name: "cookbook_guidance", table: "organization_settings");
            migrationBuilder.DropColumn(name: "relative_path", table: "snippet_suggestion_files");
            migrationBuilder.DropColumn(name: "relative_path", table: "snippet_files");
            migrationBuilder.DropColumn(name: "type", table: "snippet_suggestions");
            migrationBuilder.DropColumn(name: "type", table: "snippets");
        }
    }
}
