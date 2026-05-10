# AL Dev Toolbox

Internal Blazor Server tool for generating Microsoft Dynamics 365 Business Central (AL) workspace skeletons and standalone extensions from runtime templates.

The end-user surface is two forms:

- **New Workspace** — pick a runtime template, name the project, tick the modules you need, and click **Generate** to download a multi-folder workspace ZIP.
- **New Extension** — generate a single standalone extension folder ZIP that drops into an existing workspace, including a dependency picker fed by a well-known catalogue.

The admin surface lets a small team curate the templates, modules, and catalogue that drive those generators, with full audit history and a TOML export for backups.

The design lives under [`.design/`](./.design/). Read those before non-trivial changes; [`CLAUDE.md`](./CLAUDE.md) covers conventions and the architectural fences to stay inside.

## Stack

- .NET 10, Blazor Server (interactive server render mode where needed).
- EF Core 10 + SQLite. One file, one volume.
- Tomlyn for the seed format. Lucide.Blazor for icons.
- No client-side framework beyond Blazor itself.

## Run locally

Requires the .NET 10 SDK.

```bash
# From the repo root.
ASPNETCORE_ENVIRONMENT=Development \
ADMIN_PASSWORD=letmein \
dotnet run --project ALDevToolbox
```

Then visit <http://localhost:5000> (the port comes from `ALDevToolbox/Properties/launchSettings.json`).

Run the tests with `dotnet test` from the repo root — same workflow CI uses.

On first start the app:

1. Creates `app.db` in the content root.
2. Runs EF Core migrations.
3. Seeds runtime templates, modules, and the catalogue from `Templates.seed/` if the database is empty.

Re-runs reuse the same `app.db`. Delete the file to start over from a clean seed.

### Sign in to admin

The admin section (`/admin`, plus its children) is gated by a single shared password. Set `ADMIN_PASSWORD` (dev) or `ADMIN_PASSWORD_FILE` (production). With neither set, sign-in fails closed and the gated routes stay locked.

The end-user generators (`/projects/new`, `/projects/extension`, `/templates`) are unauthenticated.

## Run in Docker

```bash
# Build and start the container; data persists in ./data on the host.
HOST_PORT=8080 ADMIN_PASSWORD=letmein docker compose up --build
```

That starts the app on <http://localhost:8080>, with the SQLite file at `./data/app.db` on the host.

For a hand-built image:

```bash
docker build -t aldevtoolbox:latest .
docker run --rm -p 8080:8080 \
    -v "$(pwd)/data:/data" \
    -e ADMIN_PASSWORD=letmein \
    aldevtoolbox:latest
```

In production, prefer `ADMIN_PASSWORD_FILE` over `ADMIN_PASSWORD` so the password isn't visible in `docker inspect`. The Compose file forwards both if set; the app prefers the file.

| Variable                 | Purpose                                       | Default                |
|--------------------------|-----------------------------------------------|------------------------|
| `ADMIN_PASSWORD`         | Shared admin password (dev / quickstart)      | none                   |
| `ADMIN_PASSWORD_FILE`    | Path to a file containing the password (prod) | none                   |
| `DB_PATH`                | SQLite file path                              | `./app.db` / `/data/app.db` in Docker |
| `SEED_PATH`              | First-run seed directory                      | `./Templates.seed` / `/app/Templates.seed` in Docker |
| `ASPNETCORE_URLS`        | Standard ASP.NET Core binding                 | `http://+:8080`        |
| `ASPNETCORE_ENVIRONMENT` | Standard ASP.NET Core environment             | `Production`           |

The container terminates HTTP only — run TLS at the reverse proxy. `app.UseForwardedHeaders()` is wired so cookies pick up `Secure` correctly behind a proxy.

## Health checks

Two unauthenticated endpoints, suitable for a load balancer or `docker compose` healthcheck:

- `GET /health` — liveness. 200 if the process is responding.
- `GET /health/ready` — readiness. 200 if the SQLite database is reachable.

The Dockerfile's `HEALTHCHECK` polls `/health/ready`.

## Project layout

```
Components/    Razor pages, layout, shared components
Services/     Application services (generation, seed, admin CRUD)
Domain/       EF entities, value objects, plans
Data/         AppDbContext, design-time factory, migrations
Resources/    Embedded static assets (logo, ruleset, .gitignore)
Templates.seed/  First-run seed data (templates, modules, catalogue)
wwwroot/      Global CSS, theme + generate companion JS
```

See [`.design/architecture.md`](./.design/architecture.md) for what belongs where, and [`CLAUDE.md`](./CLAUDE.md) for the conventions new code should match.

## Day-to-day editing

- **Template / module / catalogue edits** happen in the admin UI. The DB is the source of truth at runtime.
- **`Templates.seed/`** seeds an empty database on first run and is the target of the **Export to TOML** button. Nothing watches it at runtime.
- **Migrations** are committed to the repo. New schema changes are added via `dotnet ef migrations add <Name>`.

## Contributing

- One milestone per PR, or a coherent slice. The build sequence is in [`.design/milestones.md`](./.design/milestones.md).
- Run the GitHub Actions build on every push — it's the floor for "compiles and starts."
- Manual smoke test the end-user flows after any change to shared services. Bring up the app under Docker before merging anything that touches startup, env vars, or volumes.
