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
    /// </summary>
    public partial class RenameSnippetsToCookbook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- recipe_suggestion_files ---
            migrationBuilder.DropForeignKey(
                name: "FK_snippet_suggestion_files_snippet_suggestions_snippet_sugges~",
                table: "snippet_suggestion_files");
            migrationBuilder.DropForeignKey(
                name: "FK_snippet_suggestion_files_organizations_organization_id",
                table: "snippet_suggestion_files");
            migrationBuilder.DropPrimaryKey(name: "PK_snippet_suggestion_files", table: "snippet_suggestion_files");
            migrationBuilder.RenameTable(name: "snippet_suggestion_files", newName: "recipe_suggestion_files");
            migrationBuilder.RenameColumn(
                name: "snippet_suggestion_id",
                table: "recipe_suggestion_files",
                newName: "recipe_suggestion_id");
            migrationBuilder.RenameIndex(
                name: "IX_snippet_suggestion_files_organization_id_snippet_suggestion~",
                table: "recipe_suggestion_files",
                newName: "IX_recipe_suggestion_files_organization_id_recipe_suggestion_i~");
            migrationBuilder.RenameIndex(
                name: "IX_snippet_suggestion_files_snippet_suggestion_id",
                table: "recipe_suggestion_files",
                newName: "IX_recipe_suggestion_files_recipe_suggestion_id");
            migrationBuilder.AddColumn<string>(
                name: "relative_path",
                table: "recipe_suggestion_files",
                type: "text",
                nullable: false,
                defaultValue: string.Empty);
            migrationBuilder.AddPrimaryKey(
                name: "PK_recipe_suggestion_files",
                table: "recipe_suggestion_files",
                column: "id");

            // --- recipe_suggestions ---
            migrationBuilder.DropForeignKey(
                name: "FK_snippet_suggestions_organizations_organization_id",
                table: "snippet_suggestions");
            migrationBuilder.DropForeignKey(
                name: "FK_snippet_suggestions_snippets_approved_snippet_id",
                table: "snippet_suggestions");
            migrationBuilder.DropForeignKey(
                name: "FK_snippet_suggestions_users_decided_by_user_id",
                table: "snippet_suggestions");
            migrationBuilder.DropForeignKey(
                name: "FK_snippet_suggestions_users_suggested_by_user_id",
                table: "snippet_suggestions");
            migrationBuilder.DropForeignKey(
                name: "FK_snippet_suggestions_application_versions_minimum_applicatio~",
                table: "snippet_suggestions");
            migrationBuilder.DropPrimaryKey(name: "PK_snippet_suggestions", table: "snippet_suggestions");
            migrationBuilder.RenameTable(name: "snippet_suggestions", newName: "recipe_suggestions");
            migrationBuilder.RenameColumn(
                name: "approved_snippet_id",
                table: "recipe_suggestions",
                newName: "approved_recipe_id");
            migrationBuilder.RenameIndex(
                name: "IX_snippet_suggestions_approved_snippet_id",
                table: "recipe_suggestions",
                newName: "IX_recipe_suggestions_approved_recipe_id");
            migrationBuilder.RenameIndex(
                name: "IX_snippet_suggestions_decided_by_user_id",
                table: "recipe_suggestions",
                newName: "IX_recipe_suggestions_decided_by_user_id");
            migrationBuilder.RenameIndex(
                name: "IX_snippet_suggestions_minimum_application_version_id",
                table: "recipe_suggestions",
                newName: "IX_recipe_suggestions_minimum_application_version_id");
            migrationBuilder.RenameIndex(
                name: "IX_snippet_suggestions_organization_id_decision",
                table: "recipe_suggestions",
                newName: "IX_recipe_suggestions_organization_id_decision");
            migrationBuilder.RenameIndex(
                name: "IX_snippet_suggestions_suggested_by_user_id",
                table: "recipe_suggestions",
                newName: "IX_recipe_suggestions_suggested_by_user_id");
            migrationBuilder.AddColumn<int>(
                name: "type",
                table: "recipe_suggestions",
                type: "integer",
                nullable: false,
                defaultValue: 0);
            migrationBuilder.AddPrimaryKey(
                name: "PK_recipe_suggestions",
                table: "recipe_suggestions",
                column: "id");

            // --- recipe_files ---
            migrationBuilder.DropForeignKey(
                name: "FK_snippet_files_organizations_organization_id",
                table: "snippet_files");
            migrationBuilder.DropForeignKey(
                name: "FK_snippet_files_snippets_snippet_id",
                table: "snippet_files");
            migrationBuilder.DropPrimaryKey(name: "PK_snippet_files", table: "snippet_files");
            migrationBuilder.RenameTable(name: "snippet_files", newName: "recipe_files");
            migrationBuilder.RenameColumn(name: "snippet_id", table: "recipe_files", newName: "recipe_id");
            migrationBuilder.RenameIndex(
                name: "IX_snippet_files_organization_id_snippet_id_ordering",
                table: "recipe_files",
                newName: "IX_recipe_files_organization_id_recipe_id_ordering");
            migrationBuilder.RenameIndex(
                name: "IX_snippet_files_snippet_id",
                table: "recipe_files",
                newName: "IX_recipe_files_recipe_id");
            migrationBuilder.AddColumn<string>(
                name: "relative_path",
                table: "recipe_files",
                type: "text",
                nullable: false,
                defaultValue: string.Empty);
            migrationBuilder.AddPrimaryKey(name: "PK_recipe_files", table: "recipe_files", column: "id");

            // --- recipes ---
            migrationBuilder.DropForeignKey(
                name: "FK_snippets_organizations_organization_id",
                table: "snippets");
            migrationBuilder.DropForeignKey(
                name: "FK_snippets_application_versions_minimum_application_version_id",
                table: "snippets");
            migrationBuilder.DropPrimaryKey(name: "PK_snippets", table: "snippets");
            migrationBuilder.RenameTable(name: "snippets", newName: "recipes");
            migrationBuilder.RenameIndex(
                name: "IX_snippets_minimum_application_version_id",
                table: "recipes",
                newName: "IX_recipes_minimum_application_version_id");
            migrationBuilder.RenameIndex(
                name: "IX_snippets_organization_id_title",
                table: "recipes",
                newName: "IX_recipes_organization_id_title");
            // Postgres trigram-GIN index from the AddSnippets migration. It
            // sits on the same columns regardless of the table's new name; we
            // rename it for consistency only.
            migrationBuilder.Sql(
                "ALTER INDEX IF EXISTS \"IX_snippets_title_description_keywords\" RENAME TO \"IX_recipes_title_description_keywords\";");
            migrationBuilder.AddColumn<int>(
                name: "type",
                table: "recipes",
                type: "integer",
                nullable: false,
                defaultValue: 0);
            migrationBuilder.AddPrimaryKey(name: "PK_recipes", table: "recipes", column: "id");

            // Re-create the foreign keys with the new naming.
            migrationBuilder.AddForeignKey(
                name: "FK_recipes_organizations_organization_id",
                table: "recipes",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(
                name: "FK_recipes_application_versions_minimum_application_version_id",
                table: "recipes",
                column: "minimum_application_version_id",
                principalTable: "application_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_recipe_files_organizations_organization_id",
                table: "recipe_files",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(
                name: "FK_recipe_files_recipes_recipe_id",
                table: "recipe_files",
                column: "recipe_id",
                principalTable: "recipes",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_recipe_suggestions_organizations_organization_id",
                table: "recipe_suggestions",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(
                name: "FK_recipe_suggestions_recipes_approved_recipe_id",
                table: "recipe_suggestions",
                column: "approved_recipe_id",
                principalTable: "recipes",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
            migrationBuilder.AddForeignKey(
                name: "FK_recipe_suggestions_users_decided_by_user_id",
                table: "recipe_suggestions",
                column: "decided_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
            migrationBuilder.AddForeignKey(
                name: "FK_recipe_suggestions_users_suggested_by_user_id",
                table: "recipe_suggestions",
                column: "suggested_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
            migrationBuilder.AddForeignKey(
                name: "FK_recipe_suggestions_application_versions_minimum_application~",
                table: "recipe_suggestions",
                column: "minimum_application_version_id",
                principalTable: "application_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_recipe_suggestion_files_organizations_organization_id",
                table: "recipe_suggestion_files",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(
                name: "FK_recipe_suggestion_files_recipe_suggestions_recipe_suggestio~",
                table: "recipe_suggestion_files",
                column: "recipe_suggestion_id",
                principalTable: "recipe_suggestions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            // Type discriminator index for the chip-row filter on
            // /cookbook and /admin/cookbook.
            migrationBuilder.CreateIndex(
                name: "IX_recipes_organization_id_type",
                table: "recipes",
                columns: new[] { "organization_id", "type" });

            // Cookbook authoring guidance for MCP agents.
            migrationBuilder.AddColumn<string>(
                name: "cookbook_guidance",
                table: "organization_settings",
                type: "text",
                nullable: false,
                defaultValue: string.Empty);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "cookbook_guidance", table: "organization_settings");

            migrationBuilder.DropIndex(name: "IX_recipes_organization_id_type", table: "recipes");

            migrationBuilder.DropForeignKey(
                name: "FK_recipe_suggestion_files_recipe_suggestions_recipe_suggestio~",
                table: "recipe_suggestion_files");
            migrationBuilder.DropForeignKey(
                name: "FK_recipe_suggestion_files_organizations_organization_id",
                table: "recipe_suggestion_files");
            migrationBuilder.DropForeignKey(
                name: "FK_recipe_suggestions_application_versions_minimum_application~",
                table: "recipe_suggestions");
            migrationBuilder.DropForeignKey(
                name: "FK_recipe_suggestions_users_suggested_by_user_id",
                table: "recipe_suggestions");
            migrationBuilder.DropForeignKey(
                name: "FK_recipe_suggestions_users_decided_by_user_id",
                table: "recipe_suggestions");
            migrationBuilder.DropForeignKey(
                name: "FK_recipe_suggestions_recipes_approved_recipe_id",
                table: "recipe_suggestions");
            migrationBuilder.DropForeignKey(
                name: "FK_recipe_suggestions_organizations_organization_id",
                table: "recipe_suggestions");
            migrationBuilder.DropForeignKey(
                name: "FK_recipe_files_recipes_recipe_id",
                table: "recipe_files");
            migrationBuilder.DropForeignKey(
                name: "FK_recipe_files_organizations_organization_id",
                table: "recipe_files");
            migrationBuilder.DropForeignKey(
                name: "FK_recipes_application_versions_minimum_application_version_id",
                table: "recipes");
            migrationBuilder.DropForeignKey(
                name: "FK_recipes_organizations_organization_id",
                table: "recipes");

            migrationBuilder.DropPrimaryKey(name: "PK_recipes", table: "recipes");
            migrationBuilder.DropColumn(name: "type", table: "recipes");
            migrationBuilder.Sql(
                "ALTER INDEX IF EXISTS \"IX_recipes_title_description_keywords\" RENAME TO \"IX_snippets_title_description_keywords\";");
            migrationBuilder.RenameIndex(
                name: "IX_recipes_organization_id_title",
                table: "recipes",
                newName: "IX_snippets_organization_id_title");
            migrationBuilder.RenameIndex(
                name: "IX_recipes_minimum_application_version_id",
                table: "recipes",
                newName: "IX_snippets_minimum_application_version_id");
            migrationBuilder.RenameTable(name: "recipes", newName: "snippets");
            migrationBuilder.AddPrimaryKey(name: "PK_snippets", table: "snippets", column: "id");

            migrationBuilder.DropPrimaryKey(name: "PK_recipe_files", table: "recipe_files");
            migrationBuilder.DropColumn(name: "relative_path", table: "recipe_files");
            migrationBuilder.RenameIndex(
                name: "IX_recipe_files_recipe_id",
                table: "recipe_files",
                newName: "IX_snippet_files_snippet_id");
            migrationBuilder.RenameIndex(
                name: "IX_recipe_files_organization_id_recipe_id_ordering",
                table: "recipe_files",
                newName: "IX_snippet_files_organization_id_snippet_id_ordering");
            migrationBuilder.RenameColumn(name: "recipe_id", table: "recipe_files", newName: "snippet_id");
            migrationBuilder.RenameTable(name: "recipe_files", newName: "snippet_files");
            migrationBuilder.AddPrimaryKey(name: "PK_snippet_files", table: "snippet_files", column: "id");

            migrationBuilder.DropPrimaryKey(name: "PK_recipe_suggestions", table: "recipe_suggestions");
            migrationBuilder.DropColumn(name: "type", table: "recipe_suggestions");
            migrationBuilder.RenameIndex(
                name: "IX_recipe_suggestions_suggested_by_user_id",
                table: "recipe_suggestions",
                newName: "IX_snippet_suggestions_suggested_by_user_id");
            migrationBuilder.RenameIndex(
                name: "IX_recipe_suggestions_organization_id_decision",
                table: "recipe_suggestions",
                newName: "IX_snippet_suggestions_organization_id_decision");
            migrationBuilder.RenameIndex(
                name: "IX_recipe_suggestions_minimum_application_version_id",
                table: "recipe_suggestions",
                newName: "IX_snippet_suggestions_minimum_application_version_id");
            migrationBuilder.RenameIndex(
                name: "IX_recipe_suggestions_decided_by_user_id",
                table: "recipe_suggestions",
                newName: "IX_snippet_suggestions_decided_by_user_id");
            migrationBuilder.RenameIndex(
                name: "IX_recipe_suggestions_approved_recipe_id",
                table: "recipe_suggestions",
                newName: "IX_snippet_suggestions_approved_snippet_id");
            migrationBuilder.RenameColumn(
                name: "approved_recipe_id",
                table: "recipe_suggestions",
                newName: "approved_snippet_id");
            migrationBuilder.RenameTable(name: "recipe_suggestions", newName: "snippet_suggestions");
            migrationBuilder.AddPrimaryKey(name: "PK_snippet_suggestions", table: "snippet_suggestions", column: "id");

            migrationBuilder.DropPrimaryKey(name: "PK_recipe_suggestion_files", table: "recipe_suggestion_files");
            migrationBuilder.DropColumn(name: "relative_path", table: "recipe_suggestion_files");
            migrationBuilder.RenameIndex(
                name: "IX_recipe_suggestion_files_recipe_suggestion_id",
                table: "recipe_suggestion_files",
                newName: "IX_snippet_suggestion_files_snippet_suggestion_id");
            migrationBuilder.RenameIndex(
                name: "IX_recipe_suggestion_files_organization_id_recipe_suggestion_i~",
                table: "recipe_suggestion_files",
                newName: "IX_snippet_suggestion_files_organization_id_snippet_suggestion~");
            migrationBuilder.RenameColumn(
                name: "recipe_suggestion_id",
                table: "recipe_suggestion_files",
                newName: "snippet_suggestion_id");
            migrationBuilder.RenameTable(name: "recipe_suggestion_files", newName: "snippet_suggestion_files");
            migrationBuilder.AddPrimaryKey(
                name: "PK_snippet_suggestion_files",
                table: "snippet_suggestion_files",
                column: "id");

            migrationBuilder.AddForeignKey(
                name: "FK_snippets_organizations_organization_id",
                table: "snippets",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(
                name: "FK_snippets_application_versions_minimum_application_version_id",
                table: "snippets",
                column: "minimum_application_version_id",
                principalTable: "application_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
            migrationBuilder.AddForeignKey(
                name: "FK_snippet_files_organizations_organization_id",
                table: "snippet_files",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(
                name: "FK_snippet_files_snippets_snippet_id",
                table: "snippet_files",
                column: "snippet_id",
                principalTable: "snippets",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(
                name: "FK_snippet_suggestions_organizations_organization_id",
                table: "snippet_suggestions",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(
                name: "FK_snippet_suggestions_snippets_approved_snippet_id",
                table: "snippet_suggestions",
                column: "approved_snippet_id",
                principalTable: "snippets",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
            migrationBuilder.AddForeignKey(
                name: "FK_snippet_suggestions_users_decided_by_user_id",
                table: "snippet_suggestions",
                column: "decided_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
            migrationBuilder.AddForeignKey(
                name: "FK_snippet_suggestions_users_suggested_by_user_id",
                table: "snippet_suggestions",
                column: "suggested_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
            migrationBuilder.AddForeignKey(
                name: "FK_snippet_suggestions_application_versions_minimum_applicatio~",
                table: "snippet_suggestions",
                column: "minimum_application_version_id",
                principalTable: "application_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
            migrationBuilder.AddForeignKey(
                name: "FK_snippet_suggestion_files_organizations_organization_id",
                table: "snippet_suggestion_files",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(
                name: "FK_snippet_suggestion_files_snippet_suggestions_snippet_sugges~",
                table: "snippet_suggestion_files",
                column: "snippet_suggestion_id",
                principalTable: "snippet_suggestions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
