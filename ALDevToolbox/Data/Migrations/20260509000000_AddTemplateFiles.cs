using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Adds the <c>template_files</c> table that holds per-folder file content
    /// (Milestone 8.5). For existing deployments, walks the on-disk
    /// <c>Templates.seed/runtime-*/examples/&lt;example_path&gt;/</c> tree and
    /// inserts one row per file into every <c>template_folders</c> row that
    /// pointed at that example. Drops <c>example_path</c> last, since it's the
    /// join key the backfill needs.
    /// </summary>
    public partial class AddTemplateFiles : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "template_files",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    template_folder_id = table.Column<int>(type: "INTEGER", nullable: false),
                    ordering = table.Column<int>(type: "INTEGER", nullable: false),
                    path = table.Column<string>(type: "TEXT", nullable: false),
                    content = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_template_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_template_files_template_folders_template_folder_id",
                        column: x => x.template_folder_id,
                        principalTable: "template_folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_template_files_template_folder_id_ordering",
                table: "template_files",
                columns: new[] { "template_folder_id", "ordering" });

            migrationBuilder.CreateIndex(
                name: "IX_template_files_template_folder_id_path",
                table: "template_files",
                columns: new[] { "template_folder_id", "path" },
                unique: true);

            BackfillFromDisk(migrationBuilder);

            migrationBuilder.DropColumn(
                name: "example_path",
                table: "template_folders");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "example_path",
                table: "template_folders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.DropTable(
                name: "template_files");
        }

        /// <summary>
        /// Best-effort port of the on-disk <c>Templates.seed/runtime-*/examples/</c>
        /// tree into the new table. Skipped silently if the seed root can't be
        /// found — fresh deployments populate via <c>SeedService</c> on first run
        /// instead, so a missing seed path on a non-empty DB only means existing
        /// folders lose their example contents until an admin re-adds them.
        /// </summary>
        private static void BackfillFromDisk(MigrationBuilder migrationBuilder)
        {
            var seedRoot = ResolveSeedPath();
            if (seedRoot is null) return;

            foreach (var runtimeDir in Directory.EnumerateDirectories(seedRoot, "runtime-*"))
            {
                var templateKey = Path.GetFileName(runtimeDir);
                var examplesRoot = Path.Combine(runtimeDir, "examples");
                if (!Directory.Exists(examplesRoot)) continue;

                foreach (var exampleDir in Directory.EnumerateDirectories(examplesRoot))
                {
                    var examplePath = Path.GetFileName(exampleDir);
                    var files = Directory.EnumerateFiles(exampleDir, "*", SearchOption.AllDirectories)
                        .Select(f => (
                            Relative: Path.GetRelativePath(exampleDir, f).Replace(Path.DirectorySeparatorChar, '/'),
                            Content: File.ReadAllText(f)))
                        .OrderBy(f => f.Relative, StringComparer.Ordinal)
                        .ToList();

                    var ordering = 0;
                    foreach (var (relative, content) in files)
                    {
                        migrationBuilder.Sql(
                            "INSERT INTO template_files (template_folder_id, ordering, path, content) " +
                            "SELECT tf.id, " + ordering + ", " + Quote(relative) + ", " + Quote(content) + " " +
                            "FROM template_folders tf " +
                            "JOIN runtime_templates rt ON rt.id = tf.template_id " +
                            "WHERE rt.key = " + Quote(templateKey) + " AND tf.example_path = " + Quote(examplePath) + ";");
                        ordering++;
                    }
                }
            }
        }

        /// <summary>SQLite single-quoted string literal — doubles up embedded apostrophes.</summary>
        private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

        /// <summary>
        /// Mirrors <c>SeedService.ResolveSeedPath</c>: honours <c>SEED_PATH</c>
        /// when present, otherwise walks up from the current directory looking
        /// for a <c>Templates.seed/</c> sibling.
        /// </summary>
        private static string ResolveSeedPath()
        {
            var fromEnv = Environment.GetEnvironmentVariable("SEED_PATH");
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return Directory.Exists(fromEnv) ? Path.GetFullPath(fromEnv) : null;
            }

            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "Templates.seed");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
