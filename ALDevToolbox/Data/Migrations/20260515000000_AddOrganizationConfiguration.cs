using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Milestone P3.14 — moves per-organisation configuration into the database
    /// so it can be edited through <c>/admin/configuration</c>:
    /// <list type="bullet">
    ///   <item><c>organization_settings</c> — one row per org with the
    ///         publisher / id-range / brief / description defaults the New
    ///         Workspace and New Extension forms pre-fill from.</item>
    ///   <item><c>organization_assets</c> — one logo per org, stored as a
    ///         BLOB. <c>kind</c> is currently always <c>Logo</c>; new kinds
    ///         can be added without a schema change.</item>
    ///   <item><c>organization_files</c> — admin-defined always-included text
    ///         files written into every workspace generated for the org.</item>
    /// </list>
    /// The pre-existing Default org is backfilled in this migration so the
    /// post-deploy state is identical to the pre-deploy state. New orgs created
    /// via signup pull their config from <c>Templates.seed/organization-defaults/</c>
    /// the first time their seed runs (see <see cref="Services.SeedService"/>).
    /// The ruleset and <c>.gitignore</c> stay as embedded resources for now —
    /// they're per-deployment policy, not per-org config, and have no obvious
    /// admin-facing customisation story yet (per <c>milestones.md</c>).
    /// </summary>
    public partial class AddOrganizationConfiguration : Migration
    {
        // Default Core id range used pre-M14, kept here so the Default org's
        // backfill row matches what the previously-hardcoded forms produced.
        private const int DefaultIdRangeFrom = 90000;
        private const int DefaultIdRangeTo = 90999;

        // The logo embedded as Resources/logo.png pre-M14. Inlined here so the
        // Default org's logo asset is populated at migration time without
        // depending on the file being present at runtime — Resources/logo.png
        // is removed from the project as part of this milestone.
        private const string DefaultLogoPngBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAEQ0lEQVR4XsVWTWxMURQ+XgaqagiFRkwjQcRCoxIrdKkJDFY0RRdUbEotREOMaFiAsWtZgEAkpEGiK6YsqZBIRRFGiqCio+0U8Jzv5n7JzctkzDTwJSd59+fdc853fu4d4vu+/E+E5B9hYGCgVAcnVXpU4gUFBQnMe39JWVQlprLFmY6rLFJZrnKTawjBn1a+XgcnnKkalTEqRzBo2vNeNu0uxmdKpdT7kxSrtFD53UQ/l2BQDINLxz7K7Wu98qh9AMOwSjSUR/wgFZIZZZZaSff+lNOHP0j3628yr6JQLO2S7Pwql49/FKDtWp/MKi+QbAZAKWjbYj2ISA64pd7By+4332XlhrECUHlj7WshwAANz2gAEsTSFqZXyc4v5qB+/SZcBTg03cc1kYVLiwRoPZ+CUe6aMdAiEgp6bbN1HS29fv6TtLf1Sz6YNXeE+bfRshEEnRpZ5LEKhAYkEDMsIltdxSNHeRKZMcz1ml4NCvvOTjHnhRzlcSrfq/F6+eSrAOWLCqVy9WgmDcFYGnof3fss+YLOeFY5srvOVQ6Ptx6YKPUHJ8r4kqFy5tAHJBLFjIGdzSXYh/2Db8W2RWq8U1DOg2GlUdR6ISVBwGvMgyE0FuxvqOrKOUcs2jzrfeS9JgvrtLp+HJTDU6MkG5AnYK14csj8lwPccN73bJ3Lrau9AozXgxavDjO2nDM0n70zjSLHb5TK1OkmjmAN+/Gf2fsbsEEBCY/dja2zUpUjF1C/VL4fGTtzuFFCuaue71LaqRBMgUX8/xv6mYApvRFb8HeEXpCeDqeprNo4Vvr1u2FNF+d4kCxcUmSoZ623qxM4PBvAkpt3HpuCWx6ocQJKEJ4cah5dEg5k8570g944DTDU/W2gTGtjEzjEg+QFDSBtQmOKS0ICMDfQ14N1Tk/RIwj8RzaDqNVSRbgUD1R5jPOeayG7Gw/nHV6oa6hz1DxoxGHIDYRqrZZetRWEC70kk3JSz6ojJJ1OJ1T8xs3P/RWzH/oN1U99jA9vT2JspC7a6SeffcK8ke63fWa9an6H33rxLef8M/FX2E9x1yE9KmW4e1wZopO4eo/gLm/WCwjY2TRZwzJcdlR1McNZkmCKFZMV6BGbNOYsOZS7Un9fCCcELcx2KOC7Dag/OAkHufd4VuU0EpTvPzeFMQd6qDwIz2bjKRX0dCpie0WzwesGB2dVukAdQLc8emWqzNNcwR2CZmUxxrw1guCr2C7CkDBDwcSs3maSixWCt17GauAbAUmIhsT/LFag60kQrAJd7LEtOQVloBBA84ExdcteGo+Sj78I4VZJk92jtyH2oGKgnLGvofJMCL6IorZFhuFtc+wdL6Sckq5yTdj1OqkSZexzMYBGlFkj5pB2UNrR/lmbzA8h2IRKNcvLKwqZcMQelbhlVvIxIPgyhkQkNyRhOIRtFhisAQSfa5QgoAgUJ4JU52XA/8QvRgye1rf4g4QAAAAASUVORK5CYII=";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organization_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    organization_id = table.Column<int>(type: "INTEGER", nullable: false),
                    default_publisher = table.Column<string>(type: "TEXT", nullable: false),
                    default_id_range_from = table.Column<int>(type: "INTEGER", nullable: false),
                    default_id_range_to = table.Column<int>(type: "INTEGER", nullable: false),
                    default_brief = table.Column<string>(type: "TEXT", nullable: false),
                    default_core_description = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<System.DateTime>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_settings", x => x.id);
                    table.ForeignKey(
                        name: "FK_organization_settings_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_organization_settings_organization_id",
                table: "organization_settings",
                column: "organization_id",
                unique: true);

            migrationBuilder.CreateTable(
                name: "organization_assets",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    organization_id = table.Column<int>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    content_type = table.Column<string>(type: "TEXT", nullable: false),
                    content = table.Column<byte[]>(type: "BLOB", nullable: false),
                    updated_at = table.Column<System.DateTime>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_assets", x => x.id);
                    table.ForeignKey(
                        name: "FK_organization_assets_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_organization_assets_organization_id_kind",
                table: "organization_assets",
                columns: new[] { "organization_id", "kind" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "organization_files",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    organization_id = table.Column<int>(type: "INTEGER", nullable: false),
                    path = table.Column<string>(type: "TEXT", nullable: false),
                    content = table.Column<string>(type: "TEXT", nullable: false),
                    mustache_enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ordering = table.Column<int>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<System.DateTime>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_organization_files_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_organization_files_organization_id_ordering",
                table: "organization_files",
                columns: new[] { "organization_id", "ordering" });
            migrationBuilder.CreateIndex(
                name: "IX_organization_files_organization_id_path",
                table: "organization_files",
                columns: new[] { "organization_id", "path" },
                unique: true);

            // Backfill the Default organisation. This is the only org pre-M14
            // (others arrived via signup post-M13 and are handled by the seed
            // path on first admin login). We pick the publisher / id range
            // hardcoded into the seed's runtime-15 template so the backfilled
            // defaults match what the user would have been pre-filled with had
            // M14 been there from the start.
            var now = System.DateTime.UtcNow.ToString("o");
            migrationBuilder.Sql(
                "INSERT INTO organization_settings ("
                + "organization_id, default_publisher, default_id_range_from, default_id_range_to, "
                + "default_brief, default_core_description, updated_at) "
                + "SELECT 1, COALESCE("
                + "  (SELECT json_extract(defaults_json, '$.publisher') FROM runtime_templates "
                + "    WHERE organization_id = 1 AND deleted_at IS NULL "
                + "    ORDER BY id LIMIT 1), ''), "
                + DefaultIdRangeFrom + ", " + DefaultIdRangeTo + ", '', '', '" + now + "' "
                + "WHERE NOT EXISTS (SELECT 1 FROM organization_settings WHERE organization_id = 1);");

            // Logo asset: insert the previously-embedded PNG bytes for the
            // Default org. Other orgs get their logo from
            // Templates.seed/organization-defaults/ on first seed.
            migrationBuilder.Sql(
                "INSERT INTO organization_assets (organization_id, kind, content_type, content, updated_at) "
                + "VALUES (1, 'Logo', 'image/png', x'" + Base64ToHex(DefaultLogoPngBase64) + "', '" + now + "') ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("organization_files");
            migrationBuilder.DropTable("organization_assets");
            migrationBuilder.DropTable("organization_settings");
        }

        /// <summary>
        /// Converts a base64-encoded blob into the uppercase hex literal SQLite
        /// expects after the <c>x'...'</c> prefix. Lets us inline binary data
        /// in a migration without writing a parameterised <c>InsertData</c>.
        /// </summary>
        private static string Base64ToHex(string base64)
        {
            var bytes = System.Convert.FromBase64String(base64);
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                sb.Append(b.ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
