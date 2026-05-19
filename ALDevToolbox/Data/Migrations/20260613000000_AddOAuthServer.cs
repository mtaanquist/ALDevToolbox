using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Stands up the OAuth 2.1 authorization server tables so the MCP endpoint
    /// can advertise DCR (RFC 7591) and CIMD to Claude.ai's directory and
    /// custom-connector flows. Four of the five tables are OpenIddict's
    /// (<c>oauth_applications</c>, <c>oauth_authorizations</c>,
    /// <c>oauth_scopes</c>, <c>oauth_tokens</c>); naming is forced to
    /// snake_case in <c>Data/Configurations/OAuth/*</c> so the schema reads
    /// like the rest of the database. The fifth, <c>oauth_consents</c>, is
    /// ours — it records "this user has trusted this client for these scopes
    /// in this org", which is how the consent screen auto-submits on
    /// repeat connections.
    ///
    /// The OpenIddict tables are intentionally outside the multi-tenant
    /// query filter: pre-auth endpoints (<c>/oauth/token</c>,
    /// <c>/oauth/register</c>) have to read them before any
    /// <c>IOrganizationContext</c> is mounted. Org attribution lives in
    /// OpenIddict's free-form <c>properties</c> JSON column. See
    /// <c>.design/mcp-oauth.md</c>.
    /// </summary>
    public partial class AddOAuthServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "oauth_applications",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    application_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    client_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    client_secret = table.Column<string>(type: "text", nullable: true),
                    client_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    consent_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    display_names = table.Column<string>(type: "text", nullable: true),
                    json_web_key_set = table.Column<string>(type: "text", nullable: true),
                    permissions = table.Column<string>(type: "text", nullable: true),
                    post_logout_redirect_uris = table.Column<string>(type: "text", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    redirect_uris = table.Column<string>(type: "text", nullable: true),
                    requirements = table.Column<string>(type: "text", nullable: true),
                    settings = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oauth_applications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "oauth_consents",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    client_id = table.Column<string>(type: "text", nullable: false),
                    scopes_granted = table.Column<string>(type: "text", nullable: false),
                    granted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oauth_consents", x => x.id);
                    table.ForeignKey(
                        name: "FK_oauth_consents_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oauth_consents_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oauth_scopes",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    descriptions = table.Column<string>(type: "text", nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    display_names = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    resources = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oauth_scopes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "oauth_authorizations",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    application_id = table.Column<string>(type: "text", nullable: true),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    creation_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    scopes = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oauth_authorizations", x => x.id);
                    table.ForeignKey(
                        name: "FK_oauth_authorizations_oauth_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "oauth_applications",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "oauth_tokens",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    application_id = table.Column<string>(type: "text", nullable: true),
                    authorization_id = table.Column<string>(type: "text", nullable: true),
                    concurrency_token = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    creation_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    expiration_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    payload = table.Column<string>(type: "text", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    redemption_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reference_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    type = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oauth_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_oauth_tokens_oauth_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "oauth_applications",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_oauth_tokens_oauth_authorizations_authorization_id",
                        column: x => x.authorization_id,
                        principalTable: "oauth_authorizations",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_oauth_applications_client_id",
                table: "oauth_applications",
                column: "client_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oauth_authorizations_application_id_status_subject_type",
                table: "oauth_authorizations",
                columns: new[] { "application_id", "status", "subject", "type" });

            migrationBuilder.CreateIndex(
                name: "IX_oauth_consents_organization_id",
                table: "oauth_consents",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_oauth_consents_user_id_client_id_organization_id",
                table: "oauth_consents",
                columns: new[] { "user_id", "client_id", "organization_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oauth_scopes_name",
                table: "oauth_scopes",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oauth_tokens_application_id_status_subject_type",
                table: "oauth_tokens",
                columns: new[] { "application_id", "status", "subject", "type" });

            migrationBuilder.CreateIndex(
                name: "IX_oauth_tokens_authorization_id",
                table: "oauth_tokens",
                column: "authorization_id");

            migrationBuilder.CreateIndex(
                name: "IX_oauth_tokens_reference_id",
                table: "oauth_tokens",
                column: "reference_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oauth_consents");

            migrationBuilder.DropTable(
                name: "oauth_scopes");

            migrationBuilder.DropTable(
                name: "oauth_tokens");

            migrationBuilder.DropTable(
                name: "oauth_authorizations");

            migrationBuilder.DropTable(
                name: "oauth_applications");
        }
    }
}
