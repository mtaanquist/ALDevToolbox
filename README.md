# AL Dev Toolbox

A self-hosted Blazor Server toolbox for Microsoft Dynamics 365 Business Central (AL) development. It bundles a suite of focused tools that a BC team can run for itself, plus a read-only MCP surface so AI agents can reach the same knowledge humans do.

The design lives under [`.design/`](./.design/). Read it before non-trivial changes. [`CLAUDE.md`](./CLAUDE.md) covers the conventions and the architectural fences to stay inside.

## What's in the box

Six end-user tools live in the sidebar's **Tools** section. Every tool requires a signed-in user; anonymous traffic redirects to `/login`.

| Tool | Route | What it does |
|------|-------|--------------|
| **Projects** | `/projects/new`, `/projects/extension` | Generate a multi-folder AL **Workspace** ZIP from a runtime template, or a single standalone **Extension** ZIP that drops into an existing workspace (with a dependency picker fed by a well-known catalogue). Browse the available templates and modules at `/templates`. |
| **Cookbook** | `/cookbook` | Reusable AL recipes (snippets, patterns, whole module skeletons), searchable by title, description, or keywords. Open a recipe to read its instructions and copy its files. Users can submit suggestions (`/cookbook/suggest`) into an admin review queue. |
| **Object Explorer** | `/object-explorer` | Browse AL source from imported BC symbol packages. Search objects, fields, and procedures across releases; follow references and implementations; and diff objects side-by-side (built for the legacy C/AL Base-vs-Customer comparison). |
| **Piper** | `/piper` | A text-transformation utility: turn comma/tab/semicolon/pipe-separated values into piped strings, SQL `IN` lists, or custom formats, with a table input mode and column selection. |
| **Translator** | `/translator` | An XLIFF (`.xlf`) translator for PTE extensions. Upload a file, translate trans-units with suggestions drawn from the org's translation memory, vote suggestions up or down, and export with formatting preserved for clean git diffs. |
| **MCP** | `/tools/mcp` | Setup page for the Model Context Protocol server (see below). Visible only when MCP is enabled site-wide and the org hasn't opted out. |

The admin surface (Editors and Admins) curates the content behind these tools: templates, modules, the dependency catalogue, application versions, cookbook recipes, object-explorer releases, and the translation memory. It carries full audit history and a TOML round-trip for backup or org-to-org transfer.

## MCP server

A read-only-leaning Model Context Protocol server is mounted at `/mcp` over OAuth, so AI clients (Claude Desktop, Claude Code, Cursor, VS Code Copilot agent mode) can use the toolbox's knowledge directly. The tools mirror the web UI:

- **Projects**: `list_templates`, `list_modules`, `list_well_known_dependencies`, `generate_workspace`, `generate_extension`.
- **Cookbook**: `search_recipes`, `get_recipe`, `get_cookbook_guidance`, `suggest_recipe`, `update_recipe_suggestion`.
- **Object Explorer**: `list_releases`, `compare_releases`, `search_objects`, `search_procedures`, `search_content`, `find_references`, `find_system_references`, `get_object_outline`, `get_procedure_source`, `list_procedure_calls`, `list_release_modules`, `download_symbol_reference`, plus the per-release translation lookups `list_translation_languages` / `search_translations`.
- **Translator**: `search_translation_memory`, `vote_translation`, `remove_translation`.

SiteAdmins toggle MCP availability on `/site-admin/settings`; each org can opt out under `/admin/administration/mcp`. The OAuth model (DCR / CIMD for hosted Claude clients, plus a static PAT bearer for desktop/CLI) is documented in [`.design/mcp-oauth.md`](./.design/mcp-oauth.md), and client setup in [`docs/mcp-clients.md`](./docs/mcp-clients.md).

## Stack

- **.NET 10**, **Blazor Server** (interactive server render where needed). No client-side framework beyond Blazor, only tiny `.razor.js` companions where unavoidable.
- **EF Core 10 + Npgsql** against **PostgreSQL 18**. The database is the single source of truth at runtime for templates, recipes, releases, translation memory, organisations, users, and settings.
- **Tomlyn** for the TOML import/export format. **Markdig** renders recipe instructions (sanitised). **DiffPlex** powers the side-by-side object diff.
- **MailKit/MimeKit** for SMTP. **OpenIddict** for the MCP OAuth surface. **Fido2.AspNet** for passkey/WebAuthn login, **Otp.NET** + **QRCoder** for TOTP MFA, **BCrypt.Net-Next** for password hashing.
- **AWSSDK.S3** and **Azure.Storage.Blobs** for off-site backup targets (chosen per deployment).
- Lucide icons are vendored as embedded SVGs under `Resources/Icons/`, with no icon NuGet dependency.

## Quickstart

The shortest path is the compose stack. The repo's [`compose.yml`](./compose.yml) defaults to the published GHCR image, so a plain `up` pulls and runs it with no local build:

```bash
# From the repo root.
HOST_PORT=8080 \
POSTGRES_PASSWORD=$(openssl rand -hex 16) \
BOOTSTRAP_ADMIN_EMAIL=admin@example.com \
BOOTSTRAP_ADMIN_PASSWORD=letmein-its-12-chars \
docker compose up
```

This brings up Postgres and the app, runs migrations, ensures the singleton **system org** exists (`Default`, flagged `IsSystem = true`, the canonical templates other orgs fork from), and creates the bootstrap admin on a fresh database. Visit <http://localhost:8080> and sign in with the bootstrap credentials.

Pin a specific release with `ALDEVTOOLBOX_TAG` (e.g. `ALDEVTOOLBOX_TAG=6.0.0`); it defaults to `latest`. To **build from source** instead, comment out `image:` and uncomment `build: .` on the `aldevtoolbox` service in `compose.yml`, then run `docker compose up --build`.

The operator runbook in [`docs/operator-runbook.md`](./docs/operator-runbook.md) covers every other deployment flow: fresh deploy, backup and restore, SMTP rotation, SiteAdmin promotion, and key-ring recovery.

## Run locally without Docker

Requires the .NET 10 SDK and a reachable PostgreSQL 18.

```bash
# Start a Postgres (or run `docker compose up db -d` against the repo's compose file).
docker run -d --name aldt-pg -e POSTGRES_PASSWORD=postgres -p 5432:5432 postgres:18-alpine

# From the repo root.
ASPNETCORE_ENVIRONMENT=Development \
ConnectionStrings__DefaultConnection="Host=localhost;Username=postgres;Password=postgres;Database=postgres" \
BOOTSTRAP_ADMIN_EMAIL=admin@example.com \
BOOTSTRAP_ADMIN_PASSWORD=letmein-its-12-chars \
dotnet run --project ALDevToolbox
```

Then visit the URL from `ALDevToolbox/Properties/launchSettings.json` (typically <http://localhost:5000>).

Run the tests with `dotnet test` from the repo root, the same workflow CI uses. Tests use Testcontainers locally (Docker required), or set `ALDT_TEST_POSTGRES_CONNECTION` to point at an already-running Postgres and skip container startup.

### What happens on first start

1. Runs EF Core migrations against the configured Postgres database.
2. Ensures the **Default** organisation exists and carries `IsSystem = true`, marking the singleton system org that holds the canonical templates other orgs fork from via `TemplateImportService`.
3. Backfills the platform per-extension files (e.g. the canonical `app.json`) for any organisation missing them.
4. Creates the bootstrap admin from `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD`, only if no users exist yet, and stamps it `IsSiteAdmin = true` so the **Site Admin** console is reachable out of the box.

Re-runs reuse the same database; every step is idempotent. Drop the database (or `docker compose down -v`) to start over.

## Accounts, roles, and signup

Authentication is email + password, scoped to organisations. Three org-scoped roles:

- **`User`**: uses the tools only.
- **`Editor`**: additionally sees the content-authoring admin pages (templates, modules, catalogue, application versions, cookbook, object explorer, translation memory). Does not see the Administration tab, Dashboard, or audit log.
- **`Admin`**: sees everything in the org, including the audit log, organisation configuration, user management, backups exposed to org admins, and everything an Editor sees.

**SiteAdmin** is a separate cross-org flag for hosting operators. It surfaces the `/site-admin/*` console (system settings, all-orgs user management, backups, MCP toggle) regardless of which org the user belongs to. Granted explicitly via `/site-admin/users`, or stamped on the bootstrap admin on a fresh database. The "last SiteAdmin" guard refuses to demote the final one.

- **Bootstrap admin.** Set `BOOTSTRAP_ADMIN_EMAIL` and `BOOTSTRAP_ADMIN_PASSWORD` on first boot. The values are read once on a fresh database and ignored after a user exists.
- **Existing-org signups** land as `Pending` and need an admin in the same org to approve them under `/admin/administration/users`. SMTP, if configured, notifies the org's admins.
- **New-org signups** auto-approve and sign the user in as that org's admin. New orgs start empty, so admins import templates on demand from `/admin/templates`, which forks the canonical content from the system org. There is no superuser. To suppress public signup, hide `/signup` at the proxy.

See [`.design/auth-and-audit.md`](./.design/auth-and-audit.md) for the full lifecycle.

### Sign-in options

- **Password** is always available, optionally backed by **TOTP** (authenticator app) or **email** MFA, configured per user.
- **Password reset** uses single-use tokens emailed via SMTP, expiring after one hour.
- **Passkeys / WebAuthn** are available when `Auth__WebAuthn__RpId` and `Auth__WebAuthn__OriginsCsv` are configured (compose passes these through from `AUTH_WEBAUTHN_RP_ID` / `AUTH_WEBAUTHN_ORIGINS`). Leave blank to disable the passkey UI; users fall back to password + optional MFA. Org-level identity options live under `/admin/administration/identity`.

## SMTP configuration

Required for signup notifications and password-reset emails. With SMTP unconfigured the app boots fine; the affected pages just say "Email is not configured; ask an admin." rather than failing silently. Send failures log a warning and never roll back the underlying action; a failed approval email shouldn't unapprove the user.

| Variable               | Purpose                                                  |
|------------------------|----------------------------------------------------------|
| `SMTP_HOST`            | SMTP relay hostname.                                     |
| `SMTP_PORT`            | SMTP port (typically `587` for STARTTLS).                |
| `SMTP_USER`            | Auth username.                                           |
| `SMTP_PASSWORD_FILE`   | Path to a file containing the SMTP password.             |
| `SMTP_FROM`            | The `From:` address on outbound mail.                    |
| `SMTP_USE_STARTTLS`    | `true` to upgrade the connection with STARTTLS.          |

SMTP can also be configured at runtime on `/site-admin/settings` (the env vars are a pre-DB fallback); the password is encrypted with the Data Protection key ring. Configuring SMTP also changes signup: `/signup` switches to an **email-first verified flow** (the visitor confirms a one-time link/code before the account is created). With SMTP unset, signup falls back to the single-form path with no email verification.

## Run in Docker

```bash
# Pull and start the stack; the database persists in the named pg-data volume.
HOST_PORT=8080 \
POSTGRES_PASSWORD=$(openssl rand -hex 16) \
BOOTSTRAP_ADMIN_EMAIL=admin@example.com \
BOOTSTRAP_ADMIN_PASSWORD=letmein-its-12-chars \
docker compose up -d
```

The container terminates HTTP only; run TLS at a reverse proxy. `app.UseForwardedHeaders()` is wired so cookies pick up `Secure` correctly behind a proxy. The stack ships sensible CPU/memory ceilings in `compose.yml`; note the app limit is **4 GiB** because Object Explorer ingestion of Microsoft's Base Application peaks at 2-3 GiB of heap (1 GiB OOMs). Lower it only if you won't import large symbol packages.

| Variable                                      | Purpose                                                   | Default                |
|-----------------------------------------------|-----------------------------------------------------------|------------------------|
| `ALDEVTOOLBOX_TAG`                            | Image tag the `aldevtoolbox` service pulls from GHCR.     | `latest`               |
| `BOOTSTRAP_ADMIN_EMAIL`                       | First admin email. Read once on a fresh database (no users yet); ignored after. | none |
| `BOOTSTRAP_ADMIN_PASSWORD`                    | First admin password. Same fresh-database-only rule.      | none                   |
| `ConnectionStrings__DefaultConnection`        | Postgres connection string (Npgsql format). Built from `POSTGRES_*` by compose. | required |
| `POSTGRES_USER` / `POSTGRES_PASSWORD` / `POSTGRES_DB` | Read by the `db` compose service. Set at least `POSTGRES_PASSWORD`. | `aldevtoolbox` |
| `SINGLE_TENANT_MODE`                          | `1` to run as a single-organisation install (see below).  | `0` (multi-tenant)     |
| `SINGLE_TENANT_ORG_NAME` / `SINGLE_TENANT_ORG_SLUG` / `SINGLE_TENANT_EMAIL_DOMAINS` | First-run-only seeding for the lone org in single-tenant mode. | none |
| `DATA_PROTECTION_KEY_DIR`                     | Where the Data Protection key ring lives (cookie auth keys, SMTP-password ciphertext). Mounted on the `app-keys` volume. | `/var/lib/aldevtoolbox/dp-keys` |
| `BACKUPS_DIR`                                 | Where `pg_dump` files land (mounted on the `app-backups` volume). | `/var/lib/aldevtoolbox/backups` |
| `DISABLE_BACKUP_SCHEDULER`                    | `1` to disable the daily `pg_dump` (+ per-tenant snapshot) scheduler. | unset            |
| `DISABLE_OE_VACUUM_SCHEDULER`                 | `1` to disable the nightly VACUUM over Object Explorer tables. | unset               |
| `DISABLE_USAGE_SNAPSHOT_SCHEDULER`            | `1` to disable the 15-minute storage-usage snapshots.     | unset                  |
| `AUTH_WEBAUTHN_RP_ID` / `AUTH_WEBAUTHN_ORIGINS` | Passkey relying-party id and comma-separated `https://` origins. Leave blank to disable the passkey UI. | unset |
| `SMTP_HOST` / `SMTP_PORT` / `SMTP_USER` / `SMTP_PASSWORD_FILE` / `SMTP_FROM` / `SMTP_USE_STARTTLS` | SMTP relay used for signup and password-reset emails. | none |
| `PG_DUMP_PATH` / `PG_RESTORE_PATH`            | Override only if the Postgres client binaries aren't on `PATH`. | on `PATH` in the image |
| `OAUTH_KEY_DIR`                               | MCP OAuth signing-key directory.                          | `DATA_PROTECTION_KEY_DIR` |
| `ASPNETCORE_URLS`                             | Standard ASP.NET Core binding.                            | `http://+:8080`        |
| `ASPNETCORE_ENVIRONMENT`                      | Standard ASP.NET Core environment.                        | `Production`           |

The annotated, copy-to-`.env` reference for all of these lives in [`.env-sample`](./.env-sample). Upgrading from a v1 (SQLite) deployment? See [`.design/migrating-from-sqlite.md`](./.design/migrating-from-sqlite.md).

## Single-tenant vs multi-tenant

By default the app is **multi-tenant**: organisations are isolated by an EF query filter, new-org signups auto-provision an org, and SiteAdmins manage storage quotas and per-tenant snapshots across all of them.

Set `SINGLE_TENANT_MODE=1` when one company hosts the toolbox for itself. The multi-tenant machinery is unnecessary in that shape, so the flag **hides and disables** it:

- **Storage quotas** are gone: the settings tab, the per-org Storage page, the sidebar capacity bar, and the usage-snapshot scheduler are all removed, and no write is ever silently blocked.
- **Per-tenant snapshots** are gone; the system-level `pg_dump` backups keep running.
- **Signup** no longer offers org creation; existing-org onboarding (claimed email domain, admin invite) still works.

The lone organisation *is* the Default/system org, so its Administration and template-authoring pages (normally hidden in the system org) are surfaced so it can manage its own content. On a fresh database (the same window the bootstrap admin uses), `SINGLE_TENANT_ORG_NAME`, `SINGLE_TENANT_ORG_SLUG`, and `SINGLE_TENANT_EMAIL_DOMAINS` seed the org's name, slug, and claimed email domains; a seeded domain turns on auto-join for verified (SMTP) signups from it. These vars are ignored after first run; later edits go through `/admin/administration/identity`.

> **One-way switch.** Single-tenant is a deployment-time choice read once at boot. Because the single tenant is the system org and self-service org creation is off, there is **no in-place path back to multi-tenant**; stand up a fresh install if you need it. The flag does *not* relax tenant isolation; it only removes surfaces. See [`.design/deployment.md`](./.design/deployment.md).

## Releases and published images

Releases are published to the GitHub Container Registry as `ghcr.io/mtaanquist/aldevtoolbox`. Each `vX.Y.Z` tag publishes the exact version plus moving `latest`, major (e.g. `6`), and minor (`6.0`) tags, so you can pin as loosely or tightly as you like. (Release versioning follows "one major per shipped tool"; see [`CLAUDE.md`](./CLAUDE.md) under *Releases and image publishing*.)

The repo's [`compose.yml`](./compose.yml) already deploys from these images: the `aldevtoolbox` service is `image: ghcr.io/mtaanquist/aldevtoolbox:${ALDEVTOOLBOX_TAG:-latest}`. So a production deployment is just that file plus a `.env`:

```bash
# Copy the annotated sample and fill in the essentials.
cp .env-sample .env
#   POSTGRES_PASSWORD=change-me-to-something-strong
#   BOOTSTRAP_ADMIN_EMAIL=admin@example.com
#   BOOTSTRAP_ADMIN_PASSWORD=letmein-its-12-chars
#   ALDEVTOOLBOX_TAG=6.0.0        # optional: pin a release; defaults to latest

docker compose up -d
docker compose pull && docker compose up -d   # later: grab a newer image and recreate
```

To build the image locally instead of pulling it, comment out `image:` and uncomment `build: .` on the `aldevtoolbox` service, then `docker compose up --build`.

## HTTPS with Caddy (optional)

[`compose.yml`](./compose.yml) ships an optional, commented-out `caddy` service that fronts the app on ports 80/443 and provisions Let's Encrypt certificates automatically. Bring-your-own Traefik/nginx still works; this is just a batteries-included path. To enable it (one-time setup):

1. Point a DNS `A`/`AAAA` record at the host and open ports **80** and **443**.
2. In `.env`, set `SITE_ADDRESS` (your domain) and `ACME_EMAIL` (for Let's Encrypt expiry notices). Both are required once the service is enabled.
3. Uncomment the `caddy` service **and** the `caddy-data` / `caddy-config` volumes in `compose.yml`.
4. *(Recommended)* remove the `aldevtoolbox` `ports:` mapping so the app is reachable only through the proxy.
5. `docker compose up -d`. Caddy issues a cert for `SITE_ADDRESS`, reverse-proxies to the app, and gates traffic on `/readyz` until startup finishes. The proxy config lives in the repo's [`Caddyfile`](./Caddyfile).

No public domain handy? Set `SITE_ADDRESS=localhost` (Caddy mints an internal-CA cert, so your browser will warn) or `SITE_ADDRESS=:80` (plain HTTP) to exercise the same service locally.

**Email links and passkeys.** Links in outbound emails are built from the request host; Caddy preserves it while the app honours `X-Forwarded-Proto`, so they render as `https://<your-domain>/`. Make sure users reach the app through the domain, not the raw `:8080` host port. To enable passkeys on the domain, set `AUTH_WEBAUTHN_RP_ID` to it and `AUTH_WEBAUTHN_ORIGINS` to `https://<your-domain>`.

## Backups

The database lives in the `pg-data` named volume; backups land in `app-backups` (`/var/lib/aldevtoolbox/backups`). Both are managed by the compose stack. SiteAdmins drive backup tooling from `/site-admin/backups`:

- **Ad-hoc backup.** *Take a backup now* runs `pg_dump -Fc` against the live database and writes a file to the backups volume.
- **Scheduled backup.** A background hosted service polls every minute and triggers a daily backup at the configured UTC time (default 02:00). Toggle the schedule and edit the time on `/site-admin/settings` under **Backups**.
- **Retention.** Configurable on the same settings page (default 14). After each backup the service prunes the oldest *unpinned* files past the retention count. Pinned backups are exempt.
- **Download / restore.** Per-row actions on the backups page. *Restore* drops the `public` schema and replays the dump in place; the app enters maintenance mode (503 for non-SiteAdmin) for the duration. Restores are audited.

`pg_dump` / `pg_restore` v18 ship inside the runtime image (from the pgdg apt repo in the Dockerfile) so they match the `postgres:18` server.

**Off-site backups.** Configure an S3-compatible **or** Azure Blob destination on `/site-admin/settings`; each successful local backup is then uploaded asynchronously, with off-site retention enforced independently of the local volume. Restore from off-site uses the same `/site-admin/backups` page. See [`docs/offsite-backups.md`](./docs/offsite-backups.md) for the full setup, including MinIO / Backblaze / R2 / Azure worked examples.

For a logical export, signed-in admins can hit **Export to TOML** under `/admin/administration/export` to download a ZIP of the org's templates, modules, catalogue, application versions, organisation settings, logo, and always-included files. The same screen accepts the import direction for backup or org-to-org transfer.

## Health checks

Two unauthenticated endpoints, suitable for a load balancer or compose healthcheck:

- `GET /healthz`: **liveness.** 200 if the database is reachable **and** the Data Protection key ring round-trips; 503 otherwise. A node that loses either should drop out of rotation.
- `GET /readyz`: **readiness.** 200 once startup work (migrations + system-org / platform-files backfill + bootstrap admin) has finished; 503 until then, so reverse proxies don't send traffic to a half-initialised container.

The Dockerfile's `HEALTHCHECK` polls `/healthz`.

## Project layout

Two projects at the repo root, wired up through [`ALDevToolbox.slnx`](./ALDevToolbox.slnx):

```
ALDevToolbox/                The Blazor Server app
  Components/                Razor pages, layout, shared components
  Endpoints/                 Minimal-API endpoint groups (Generation, MCP, OAuth, SiteAdmin, etc.)
  Services/                  Application services (generation, accounts, admin CRUD, MCP tools, Object Explorer, Translator)
  Domain/                    EF entities, value objects, plans
  Data/                      AppDbContext, design-time factory, migrations
  Resources/                 Embedded static assets (ruleset, .gitignore template, Lucide SVGs)
  wwwroot/                   Global CSS, theme + companion JS
ALDevToolbox.Tests/          xUnit + FluentAssertions; Testcontainers Postgres fixture
```

See [`.design/architecture.md`](./.design/architecture.md) for what belongs where, and [`CLAUDE.md`](./CLAUDE.md) for the conventions new code should match.

## Day-to-day editing

- **Templates, modules, catalogue, application versions, cookbook recipes, and the translation memory** are edited in the admin UI (Editor or Admin). The database is the source of truth at runtime.
- **Organisation defaults, logo, and always-included files** also live in the DB, edited under `/admin/templates` and `/admin/administration`.
- **TOML round-trip.** **Export to TOML** under `/admin/administration/export` downloads a ZIP of the org's configuration; the same screen accepts the import direction.
- **System-org import.** New or empty orgs pull canonical templates / modules / catalogue from the singleton system org (`Default`, `IsSystem = true`) via `/admin/templates`. There is no on-disk seed directory.
- **Object Explorer releases** are imported by Editors and Admins from `/admin/object-explorer`.
- **Migrations** are committed to the repo; new schema changes are added via `dotnet ef migrations add <Name>`.

## Contributing

- One coherent slice per PR. What's shipped is recorded in [`.design/completed-milestones.md`](./.design/completed-milestones.md); uncommitted ideas live in [`.design/roadmap.md`](./.design/roadmap.md).
- The GitHub Actions build runs on every push; it's the floor for "compiles, starts, and tests pass."
- Manual smoke test the end-user flows after any change to shared services. Bring the app up under Docker before merging anything that touches startup, env vars, or volumes.
- Contributions are accepted under the project's licence and may be relicensed by the maintainer; see [`CONTRIBUTING.md`](./CONTRIBUTING.md).

## License

Source-available under the [Elastic License 2.0](LICENSE): free to use, modify, and self-host for personal and internal company use, but not to offer as a hosted or managed service to third parties.
