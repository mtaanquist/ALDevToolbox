using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class OptionalRecipeDownloadCustomer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "customer_name",
                table: "recipe_downloads",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "recipe_downloads",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                // Every row that already exists came from the ZIP endpoint --
                // the copy path did not exist before this migration.
                defaultValue: "Download");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source",
                table: "recipe_downloads");

            migrationBuilder.AlterColumn<string>(
                name: "customer_name",
                table: "recipe_downloads",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
