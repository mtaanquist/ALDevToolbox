# AL Dev Toolbox

Internal Blazor Server tool for generating Microsoft Dynamics 365 Business Central (AL) workspace skeletons and standalone extensions from runtime templates.

The end-user surface is two forms:

- **New Workspace** — pick a runtime template, name the project, tick the modules you need, and click **Generate** to download a multi-folder workspace ZIP.
- **New Extension** — generate a single standalone extension folder ZIP that drops into an existing workspace, including a dependency picker fed by a well-known catalogue.

The admin surface lets a small team curate the templates, modules, catalogue, application versions, organisation defaults, logo, and always-included files that drive those generators, with full audit history and a TOML export for backups.

The design lives under [`.design/`](./.design/). Read those before non-trivial changes; [`CLAUDE.md`](./CLAUDE.md) covers conventions and the architectural fences to stay inside.

## Stack

- .NET 10, Blazor Server (interactive server render mode where needed).
- EF Core 10 + SQLite. One file, one volume.
- Tomlyn for the seed format. MailKit for SMTP. Lucide.Blazor for icons.
- No client-side framework beyond Blazor itself.

## Run locally

Requires the .NET 10 SDK.

```bash
# From the repo root.
ASPNETCORE_ENVIRONMENT=Development \
BOOTSTRAP_ADMIN_EMAIL=admin@example.com \
BOOTSTRAP_ADMIN_PASSWORD=letmein-its-12-chars \
dotnet run --project ALDevToolbox
```

Then visit <http://localhost:5000> (the port comes from `ALDevToolbox/Properties/launchSettings.json`).

Run the tests with `dotnet test` from the repo root — same workflow CI uses.

On first start the app:

1. Creates `app.db` in the content root.
2. Runs EF Core migrations.
3. Creates the **Default** organisation and seeds its templates, modules, catalogue, and organisation defaults from `Templates.seed/`.
4. Creates the bootstrap admin account from `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD` (only if no users exist yet).

Re-runs reuse the same `app.db`. Delete the file to start over.

## Accounts and signup

Authentication is email + password, scoped to organisations. Two roles: `User` (uses the generators) and `Admin` (manages templates, modules, catalogue, application versions, the audit log, organisation configuration, and other users in the same org).

- **Bootstrap admin.** Set `BOOTSTRAP_ADMIN_EMAIL` and `BOOTSTRAP_ADMIN_PASSWORD` on first boot. The values are read once on a fresh database and ignored after a user exists. See `.design/auth-and-audit.md` for the full lifecycle.
- **Existing-org signups** land as `Pending` and need an admin in the same org to approve them via `/admin/users`. SMTP, if configured, notifies the org's admins.
- **New-org signups** auto-approve, seed the org from `Templates.seed/`, and sign the user in as that org's admin. There is no superuser. To suppress public signup, hide `/signup` at the proxy.
- **Password reset** uses single-use tokens emailed via SMTP. Tokens expire after one hour.

The end-user generators (`/projects/new`, `/projects/extension`, `/templates`) require a signed-in user — anonymous traffic redirects to `/login`.

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
# Build and start the container; data persists in ./data on the host.
HOST_PORT=8080 \
BOOTSTRAP_ADMIN_EMAIL=admin@example.com \
BOOTSTRAP_ADMIN_PASSWORD=letmein-its-12-chars \
docker compose up --build
```

That starts the app on <http://localhost:8080>, with the SQLite file at `./data/app.db` on the host.

| Variable                                      | Purpose                                                   | Default                |
|-----------------------------------------------|-----------------------------------------------------------|------------------------|
| `BOOTSTRAP_ADMIN_EMAIL`                       | First admin email (only on a fresh database)              | none                   |
| `BOOTSTRAP_ADMIN_PASSWORD`                    | First admin password (only on a fresh database)           | none                   |
| `DB_PATH`                                     | SQLite file path                                          | `./app.db` / `/data/app.db` in Docker |
| `SEED_PATH`                                   | First-run seed directory                                  | `./Templates.seed` / `/app/Templates.seed` in Docker |
| `SMTP_HOST` / `SMTP_PORT` / `SMTP_USER` / `SMTP_PASSWORD_FILE` / `SMTP_FROM` / `SMTP_USE_STARTTLS` | SMTP relay used for signup and password-reset emails | none                   |
| `ASPNETCORE_URLS`                             | Standard ASP.NET Core binding                             | `http://+:8080`        |
| `ASPNETCORE_ENVIRONMENT`                      | Standard ASP.NET Core environment                         | `Production`           |

The container terminates HTTP only — run TLS at the reverse proxy. `app.UseForwardedHeaders()` is wired so cookies pick up `Secure` correctly behind a proxy.

## Backup

Single SQLite file at `DB_PATH`. Back it up by copying the file (`sqlite3 app.db ".backup app.db.bak"` is the safe way under load; a plain `cp` works if the app is stopped). Restore by replacing the file and restarting the app.

For a logical export, signed-in admins can hit **Export to TOML** under `/admin/configuration` to download a ZIP of the org's templates, modules, catalogue, application versions, organisation settings, logo, and always-included files. The same screen accepts the import direction.

## Health checks

Two unauthenticated endpoints, suitable for a load balancer or `docker compose` healthcheck:

- `GET /health` — liveness. 200 if the process is responding.
- `GET /health/ready` — readiness. 200 if the SQLite database is reachable.

The Dockerfile's `HEALTHCHECK` polls `/health/ready`.

## Project layout

```
Components/      Razor pages, layout, shared components
Services/        Application services (generation, seed, accounts, admin CRUD)
Domain/          EF entities, value objects, plans
Data/            AppDbContext, design-time factory, migrations
Resources/       Embedded static assets (ruleset, .gitignore template)
Templates.seed/  First-run seed data (templates, modules, catalogue, organisation defaults)
wwwroot/         Global CSS, theme + generate companion JS
```

See [`.design/architecture.md`](./.design/architecture.md) for what belongs where, and [`CLAUDE.md`](./CLAUDE.md) for the conventions new code should match.

## Day-to-day editing

- **Template / module / catalogue / application-version edits** happen in the admin UI. The DB is the source of truth at runtime.
- **Organisation defaults / logo / always-included files** also live in the DB, edited from `/admin/configuration`.
- **`Templates.seed/`** seeds an empty organisation on first contact and is the target of the **Export to TOML** button. Nothing watches it at runtime.
- **Migrations** are committed to the repo. New schema changes are added via `dotnet ef migrations add <Name>`.

## Contributing

- One milestone per PR, or a coherent slice. The build sequence is in [`.design/milestones.md`](./.design/milestones.md).
- Run the GitHub Actions build on every push — it's the floor for "compiles, starts, and tests pass."
- Manual smoke test the end-user flows after any change to shared services. Bring up the app under Docker before merging anything that touches startup, env vars, or volumes.
