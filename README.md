# AL Dev Toolbox

Internal Blazor Server tool for generating Microsoft Dynamics 365 Business Central (AL) workspace skeletons and standalone extensions from runtime templates.

The end-user surface is two forms:

- **New Workspace** — pick a runtime template, name the project, tick the modules you need, and click **Generate** to download a multi-folder workspace ZIP.
- **New Extension** — generate a single standalone extension folder ZIP that drops into an existing workspace, including a dependency picker fed by a well-known catalogue.

The admin surface lets a small team curate the templates, modules, catalogue, application versions, organisation defaults, logo, and always-included files that drive those generators, with full audit history and a TOML export for backups.

The design lives under [`.design/`](./.design/). Read those before non-trivial changes; [`CLAUDE.md`](./CLAUDE.md) covers conventions and the architectural fences to stay inside.

## Stack

- .NET 10, Blazor Server (interactive server render mode where needed).
- EF Core 10 + Npgsql against PostgreSQL 18. App + db as sibling compose services, named volumes for data, Data Protection keys, and `pg_dump` backups.
- Tomlyn for the TOML import/export format. MailKit for SMTP. Lucide icons vendored as embedded SVGs under `Resources/Icons/` (no NuGet dependency).
- OpenIddict for the MCP OAuth surface, `Fido2.AspNet` for passkey/WebAuthn login, `AWSSDK.S3` for off-site backups.
- No client-side framework beyond Blazor itself.

## Quickstart

The shortest path is the compose stack:

```bash
# From the repo root.
BOOTSTRAP_ADMIN_EMAIL=admin@example.com \
BOOTSTRAP_ADMIN_PASSWORD=letmein-its-12-chars \
docker compose up --build
```

This brings up Postgres and the app, runs migrations, ensures the singleton **system org** (`Default`, flagged `IsSystem = true` — the canonical templates other orgs fork from) exists, and creates the bootstrap admin on a fresh database. Visit <http://localhost:8080> and sign in with the bootstrap credentials.

Run the operator runbook in [`docs/operator-runbook.md`](./docs/operator-runbook.md) for every other deployment flow — fresh deploy, backup and restore, SMTP rotation, SiteAdmin promotion, key-ring recovery.

## Run locally without Docker

Requires the .NET 10 SDK and a reachable PostgreSQL 18. The shortest path is the compose stack above; for an out-of-container `dotnet run`, point the connection string at any Postgres you have handy.

```bash
# Start a local Postgres (or run `docker compose up db -d` against the
# repo's compose file).
docker run -d --name aldt-pg -e POSTGRES_PASSWORD=postgres -p 5432:5432 postgres:18-alpine

# From the repo root.
ASPNETCORE_ENVIRONMENT=Development \
ConnectionStrings__DefaultConnection="Host=localhost;Username=postgres;Password=postgres;Database=postgres" \
BOOTSTRAP_ADMIN_EMAIL=admin@example.com \
BOOTSTRAP_ADMIN_PASSWORD=letmein-its-12-chars \
dotnet run --project ALDevToolbox
```

Then visit <http://localhost:5000> (the port comes from `ALDevToolbox/Properties/launchSettings.json`).

Run the tests with `dotnet test` from the repo root — same workflow CI uses. Tests use Testcontainers locally (Docker required) or the `ALDT_TEST_POSTGRES_CONNECTION` env var when you have a Postgres already running and want to skip container startup.

On first start the app:

1. Runs EF Core migrations against the configured Postgres database.
2. Ensures the **Default** organisation exists and carries `IsSystem = true` — it's the singleton system org that holds the canonical templates other orgs fork from via `TemplateImportService`.
3. Backfills the platform per-extension files (e.g. canonical `app.json`) for every organisation that's missing them.
4. Creates the bootstrap admin from `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD` (only if no users exist yet). The bootstrap admin is stamped `IsSiteAdmin = true` so the **Site Admin** console is reachable out of the box.

Re-runs reuse the same database. Drop the database (or `docker compose down -v`) to start over.

## Accounts and signup

Authentication is email + password, scoped to organisations. Three org-scoped roles:

- **`User`** — uses the generators only.
- **`Editor`** — additionally sees the content-authoring admin pages (templates, modules, catalogue, snippets, app versions, object explorer). Does not see the Administration tab, Dashboard, or audit log.
- **`Admin`** — sees everything in the org: the audit log, organisation configuration, user management, backups exposed to org admins, and everything an Editor sees.

**SiteAdmin** is a separate cross-org flag for hosting operators. It surfaces the `/site-admin/*` console (system settings, all-orgs user management, backups, MCP toggle) regardless of which org the user belongs to. Granted explicitly via `/site-admin/users`, or stamped on the bootstrap admin on a fresh database. The "last SiteAdmin" guard refuses to demote the final one.

- **Bootstrap admin.** Set `BOOTSTRAP_ADMIN_EMAIL` and `BOOTSTRAP_ADMIN_PASSWORD` on first boot. The values are read once on a fresh database and ignored after a user exists. See `.design/auth-and-audit.md` for the full lifecycle.
- **Existing-org signups** land as `Pending` and need an admin in the same org to approve them via `/admin/users`. SMTP, if configured, notifies the org's admins.
- **New-org signups** auto-approve and sign the user in as that org's admin. New orgs start empty — admins import templates on demand from `/admin/templates`, which forks the canonical content from the system org via `TemplateImportService`. There is no superuser. To suppress public signup, hide `/signup` at the proxy.
- **Password reset** uses single-use tokens emailed via SMTP. Tokens expire after one hour.
- **Passkeys / WebAuthn.** Available when `Auth__WebAuthn__RpId` and `Auth__WebAuthn__OriginsCsv` are configured (compose passes these through from `AUTH_WEBAUTHN_RP_ID` / `AUTH_WEBAUTHN_ORIGINS`). Leave blank to disable the passkey UI; users fall back to password + optional TOTP / email MFA.

The end-user generators (`/projects/new`, `/projects/extension`, `/templates`) require a signed-in user — anonymous traffic redirects to `/login`.

## Beyond the generators

The codebase has grown a few read-and-author surfaces alongside the workspace and extension generators. Each has its own design doc; the summary below is just orientation.

- **MCP server.** Read-only AL knowledge tools (search objects, find references, get procedure source, list templates / snippets / well-known deps, generate workspace and extension ZIPs) exposed at `/mcp` over OAuth. SiteAdmins toggle availability on `/site-admin/settings`. See [`docs/mcp-clients.md`](./docs/mcp-clients.md) for client setup and [`.design/mcp-oauth.md`](./.design/mcp-oauth.md) for the auth model.
- **Object Explorer.** Browse imported BC symbol packages, jump between objects, and follow references and implementations across an org's installed app versions. Editors and admins import releases under `/admin/object-explorer`. See [`.design/object-explorer.md`](./.design/object-explorer.md).
- **Snippets.** Reusable AL snippets at `/snippets` with admin curation under `/admin/snippets`. Also surfaced through the MCP `search_snippets` / `get_snippet` tools so agents can reach the same content humans do.

## SMTP configuration

Required for signup notifications and password reset emails. With SMTP unconfigured the app boots fine; the affected pages just say "Email is not configured; ask an admin." rather than failing silently.

| Variable               | Purpose                                                  |
|------------------------|----------------------------------------------------------|
| `SMTP_HOST`            | SMTP relay hostname.                                     |
| `SMTP_PORT`            | SMTP port (typically `587` for STARTTLS).                |
| `SMTP_USER`            | Auth username.                                           |
| `SMTP_PASSWORD_FILE`   | Path to a file containing the SMTP password.             |
| `SMTP_FROM`            | The `From:` address on outbound mail.                    |
| `SMTP_USE_STARTTLS`    | `true` to upgrade the connection with STARTTLS.          |

Email send failures log a warning and never roll back the underlying action — a failed approval email shouldn't unapprove the user.

## Run in Docker

```bash
# Build and start the stack; database persists in the named pg-data volume.
HOST_PORT=8080 \
POSTGRES_PASSWORD=$(openssl rand -hex 16) \
BOOTSTRAP_ADMIN_EMAIL=admin@example.com \
BOOTSTRAP_ADMIN_PASSWORD=letmein-its-12-chars \
docker compose up --build
```

That starts the app on <http://localhost:8080>, with the database in the `pg-data` named volume.

| Variable                                      | Purpose                                                   | Default                |
|-----------------------------------------------|-----------------------------------------------------------|------------------------|
| `BOOTSTRAP_ADMIN_EMAIL`                       | First admin email (only on a fresh database)              | none                   |
| `BOOTSTRAP_ADMIN_PASSWORD`                    | First admin password (only on a fresh database)           | none                   |
| `ConnectionStrings__DefaultConnection`        | Postgres connection string (Npgsql format). Built from `POSTGRES_*` by compose. | none — required |
| `POSTGRES_USER` / `POSTGRES_PASSWORD` / `POSTGRES_DB` | Read by the `db` compose service. Set at least `POSTGRES_PASSWORD`. | `aldevtoolbox` |
| `DATA_PROTECTION_KEY_DIR`                     | Where the Data Protection key ring lives (cookie auth keys, SMTP-password ciphertext). Mounted on the `app-keys` volume. | `/var/lib/aldevtoolbox/dp-keys` |
| `BACKUPS_DIR`                                 | Where `pg_dump` files land (mounted on the `app-backups` volume) | `/var/lib/aldevtoolbox/backups` |
| `DISABLE_BACKUP_SCHEDULER`                    | `1` to disable the daily backup scheduler (tests / CI)    | unset                  |
| `AUTH_WEBAUTHN_RP_ID` / `AUTH_WEBAUTHN_ORIGINS` | Passkey relying-party id and comma-separated allowed origins. Leave blank to disable the passkey UI. | unset |
| `SMTP_HOST` / `SMTP_PORT` / `SMTP_USER` / `SMTP_PASSWORD_FILE` / `SMTP_FROM` / `SMTP_USE_STARTTLS` | SMTP relay used for signup and password-reset emails | none                   |
| `ASPNETCORE_URLS`                             | Standard ASP.NET Core binding                             | `http://+:8080`        |
| `ASPNETCORE_ENVIRONMENT`                      | Standard ASP.NET Core environment                         | `Production`           |

Upgrading from a v1 (SQLite) deployment? See [`migrating-from-sqlite.md`](./.design/migrating-from-sqlite.md).

The container terminates HTTP only — run TLS at the reverse proxy. `app.UseForwardedHeaders()` is wired so cookies pick up `Secure` correctly behind a proxy.

### Deploy from the published image

The compose stack above builds the app from source (`build: .`). For a deployment you don't need to build locally — releases are published to the GitHub Container Registry as `ghcr.io/mtaanquist/aldevtoolbox`. Each `vX.Y.Z` tag publishes the exact version plus moving `latest`, major (`6`), and minor (`6.0`) tags, so you can pin as loosely or tightly as you like. (Release versioning follows "one major per shipped tool" — see [`CLAUDE.md`](./CLAUDE.md) under *Releases and image publishing*.)

Drop this `compose.yaml` next to a `.env` file and run `docker compose up -d`. It's the same shape as the repo's `compose.yml`, with `build: .` swapped for the published image:

```yaml
services:
  db:
    image: postgres:18-alpine
    environment:
      POSTGRES_USER: ${POSTGRES_USER:-aldevtoolbox}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:?set a strong password}
      POSTGRES_DB: ${POSTGRES_DB:-aldevtoolbox}
    volumes:
      # Mount the parent, not .../data — postgres:18 keeps data under
      # /var/lib/postgresql/<major>/docker/ so in-place pg_upgrade works.
      - pg-data:/var/lib/postgresql
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ${POSTGRES_USER:-aldevtoolbox} -d ${POSTGRES_DB:-aldevtoolbox}"]
      interval: 10s
      timeout: 5s
      retries: 6
      start_period: 30s
    restart: unless-stopped

  aldevtoolbox:
    # Pin a release (e.g. ghcr.io/mtaanquist/aldevtoolbox:6.0.0) for
    # reproducible deploys; :latest or :6 follow newer releases automatically.
    image: ghcr.io/mtaanquist/aldevtoolbox:latest
    depends_on:
      db:
        condition: service_healthy
    ports:
      - "${HOST_PORT:-8080}:8080"
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ConnectionStrings__DefaultConnection: "Host=db;Port=5432;Database=${POSTGRES_DB:-aldevtoolbox};Username=${POSTGRES_USER:-aldevtoolbox};Password=${POSTGRES_PASSWORD}"
      # Read once on a fresh database; ignored after the first user exists.
      BOOTSTRAP_ADMIN_EMAIL: ${BOOTSTRAP_ADMIN_EMAIL:-}
      BOOTSTRAP_ADMIN_PASSWORD: ${BOOTSTRAP_ADMIN_PASSWORD:-}
      DATA_PROTECTION_KEY_DIR: /var/lib/aldevtoolbox/dp-keys
      BACKUPS_DIR: /var/lib/aldevtoolbox/backups
      # Passkeys — leave blank to disable the WebAuthn UI.
      Auth__WebAuthn__RpId: ${AUTH_WEBAUTHN_RP_ID:-}
      Auth__WebAuthn__OriginsCsv: ${AUTH_WEBAUTHN_ORIGINS:-}
    volumes:
      - app-keys:/var/lib/aldevtoolbox/dp-keys
      - app-backups:/var/lib/aldevtoolbox/backups
    # The image already declares a HEALTHCHECK against /healthz; compose picks
    # it up automatically.
    restart: unless-stopped

volumes:
  pg-data:
  app-keys:
  app-backups:
```

```bash
# .env in the same directory — at minimum set the password and bootstrap admin.
cat > .env <<'EOF'
POSTGRES_PASSWORD=change-me-to-something-strong
BOOTSTRAP_ADMIN_EMAIL=admin@example.com
BOOTSTRAP_ADMIN_PASSWORD=letmein-its-12-chars
EOF

docker compose up -d
docker compose pull   # later: grab the newest :latest (or :6) and recreate
```

Visit <http://localhost:8080> and sign in with the bootstrap credentials. The full env-var table from [Run in Docker](#run-in-docker) applies unchanged — only the image source differs.

## Backup

The database lives in the `pg-data` named volume. Backups live in `app-backups` (mounted at `/var/lib/aldevtoolbox/backups`); both are managed by the compose stack.

SiteAdmins drive backup tooling from `/site-admin/backups`:

- **Ad-hoc backup.** Click *Take a backup now* to run `pg_dump -Fc` against the live database and write a file to the backups volume.
- **Scheduled backup.** A background hosted service polls every minute and triggers a daily backup at the configured UTC time (default 02:00). Toggle the schedule and edit the time-of-day on `/site-admin/settings` under the **Backups** section. To see the schedule fire without waiting overnight, set the time a minute or two ahead of now.
- **Retention.** Configurable on the same settings page (default 14). After each backup, the service prunes the oldest *unpinned* files past the retention count. Pinned backups are exempt — pin a backup to keep it indefinitely.
- **Download / restore.** Per-row actions on the backups page. *Restore* drops the `public` schema and replays the dump in place; the app enters maintenance mode (503 for non-SiteAdmin) for the duration. Restores are audited.

`pg_dump` / `pg_restore` v18 ship inside the runtime image (installed from the pgdg apt repo in the Dockerfile) so they match the `postgres:18` server in the compose stack.

**Off-site backups.** Configure an S3-compatible destination (bucket, endpoint, credentials, prefix) on `/site-admin/settings`; each successful local backup is then uploaded asynchronously, and off-site retention is enforced independently of the local volume. Restore from off-site uses the same `/site-admin/backups` page. See [`docs/offsite-backups.md`](./docs/offsite-backups.md) for the full setup, including MinIO / Backblaze / R2 worked examples.

To test the full surface end-to-end:

```bash
HOST_PORT=8080 \
POSTGRES_PASSWORD=$(openssl rand -hex 16) \
BOOTSTRAP_ADMIN_EMAIL=admin@example.com \
BOOTSTRAP_ADMIN_PASSWORD=letmein-its-12-chars \
docker compose up --build
```

Sign in as `admin@example.com` (the bootstrap path stamps `is_site_admin = true`, so the **Site Admin** section appears in the sidebar), then visit `Site Admin → Backups`. `docker compose down -v` wipes both volumes; `docker compose down` preserves them.

For a logical export, signed-in admins can hit **Export to TOML** under `/admin/configuration` to download a ZIP of the org's templates, modules, catalogue, application versions, organisation settings, logo, and always-included files. The same screen accepts the import direction.

## Health checks

Two unauthenticated endpoints, suitable for a load balancer or `docker compose` healthcheck:

- `GET /healthz` — liveness. 200 if the database is reachable **and** the Data Protection key ring round-trips. A node that loses either should drop out of rotation.
- `GET /readyz` — readiness. 200 once startup work (EF migrations + system-org / platform-files backfill + bootstrap admin) has finished. Until then it returns 503 so reverse proxies don't send traffic to a half-initialised container.

The Dockerfile's `HEALTHCHECK` polls `/healthz`. The container is whole as long as it can reach the database and decrypt cookies; readiness gating lives in the reverse proxy.

## Project layout

Two projects at the repo root, wired up through `ALDevToolbox.slnx`:

```
ALDevToolbox/                The Blazor Server app
  Components/                Razor pages, layout, shared components
  Endpoints/                 Minimal-API endpoint groups (Generation, MCP, OAuth, SiteAdmin, …)
  Services/                  Application services (generation, accounts, admin CRUD, MCP tools, Object Explorer)
  Domain/                    EF entities, value objects, plans
  Data/                      AppDbContext, design-time factory, migrations
  Resources/                 Embedded static assets (ruleset, .gitignore template, Lucide SVGs)
  wwwroot/                   Global CSS, theme + companion JS
ALDevToolbox.Tests/          xUnit + FluentAssertions; Testcontainers Postgres fixture
```

See [`.design/architecture.md`](./.design/architecture.md) for what belongs where, and [`CLAUDE.md`](./CLAUDE.md) for the conventions new code should match.

## Day-to-day editing

- **Template / module / catalogue / application-version edits** happen in the admin UI. The DB is the source of truth at runtime.
- **Organisation defaults / logo / always-included files** also live in the DB, edited from `/admin/configuration`.
- **TOML round-trip.** The **Export to TOML** button under `/admin/configuration` downloads a ZIP of the org's templates, modules, catalogue, application versions, organisation settings, logo, and always-included files; the same screen accepts the import direction for backup or org-to-org transfer.
- **System-org import.** New or empty orgs pull canonical templates / modules / catalogue from the singleton system org (`Default`, `IsSystem = true`) via `/admin/templates`. There is no on-disk seed directory — the system org is the source of truth.
- **Migrations** are committed to the repo. New schema changes are added via `dotnet ef migrations add <Name>`.

## Contributing

- One coherent slice per PR. What's shipped is recorded in [`.design/completed-milestones.md`](./.design/completed-milestones.md); uncommitted ideas live in [`.design/roadmap.md`](./.design/roadmap.md).
- Run the GitHub Actions build on every push — it's the floor for "compiles, starts, and tests pass."
- Manual smoke test the end-user flows after any change to shared services. Bring up the app under Docker before merging anything that touches startup, env vars, or volumes.
- Contributions are accepted under the project's licence and may be relicensed by the maintainer; see [`CONTRIBUTING.md`](./CONTRIBUTING.md).

## License

Source-available under the [Elastic License 2.0](LICENSE) — free to use, modify, and self-host for personal and internal company use, but not to offer as a hosted or managed service to third parties.
