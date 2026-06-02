# Deployment

## Target environment

The app runs as two Docker containers (app + Postgres) sitting behind whatever ingress the company uses (Traefik, nginx, etc.). The previous "single container, single volume" posture relaxed in P4.16 — see `architecture.md`. The compose file in the repo (`compose.yml`) is the canonical deployment shape.

## Dockerfile

A multi-stage build producing a self-contained image:

The repo ships a working `Dockerfile` at the root; that's the canonical build. Key points:

- The image carries the application binaries and embedded `Resources/` only. There is no on-disk seed directory — the singleton system org owns the canonical templates at runtime.
- The app reads `ConnectionStrings__DefaultConnection` to find the database. There is no on-disk DB; persistence is the sibling `db` compose service backed by the `pg-data` named volume.
- Port 8080 inside the container, mapped however the host wants.

## docker-compose example

The canonical compose file lives at the repo root (`compose.yml`). It defines two services — `aldevtoolbox` (the app) and `db` (`postgres:18-alpine`) — wired together with a healthcheck-gated `depends_on`. The app reads `ConnectionStrings__DefaultConnection`, which compose builds from `POSTGRES_USER` / `POSTGRES_PASSWORD` / `POSTGRES_DB`. See `README.md` for the quick-start invocation.

## Environment variables

| Variable                  | Purpose                                             | Default                  |
|---------------------------|-----------------------------------------------------|--------------------------|
| `BOOTSTRAP_ADMIN_EMAIL`   | First admin email (only on a fresh database)       | none                     |
| `BOOTSTRAP_ADMIN_PASSWORD`| First admin password (only on a fresh database)    | none                     |
| `ConnectionStrings__DefaultConnection` | Postgres connection string (Npgsql format) | none — required          |
| `POSTGRES_USER` / `POSTGRES_PASSWORD` / `POSTGRES_DB` | Read by the `db` compose service | `aldevtoolbox` (set `POSTGRES_PASSWORD` for any real deployment) |
| `ASPNETCORE_URLS`         | Standard ASP.NET Core binding                       | `http://+:8080`          |
| `ASPNETCORE_ENVIRONMENT`  | Standard ASP.NET Core environment                   | `Production`             |

If `ConnectionStrings__DefaultConnection` is unset the app fails to start with a clear error.

## First-run behaviour

On first start against an empty database:

1. EF Core migrations run, creating all tables.
2. The migration seeds the **Default** organisation row and stamps it with `is_system = true` — making it the singleton system org other organisations fork from.
3. The bootstrap admin (from `BOOTSTRAP_ADMIN_*` env vars) is created in the Default org with `is_site_admin = true`.
4. The app starts serving requests.

The Default org's template catalogue starts empty. SiteAdmins author canonical templates via the regular `/admin/templates` pages; other organisations fork them at import time via `TemplateImportService` (wired to the "From the site catalogue" section of `/admin/templates`).

On subsequent starts the app applies any pending migrations and starts serving.

## Backups

The entire state of the app is in the Postgres database in the `pg-data` named volume. Back this up.

Backup is `pg_dump -Fc` against the `db` service. M18 ships a built-in backup hosted service that runs `pg_dump` on a schedule and writes to `/var/lib/aldevtoolbox/backups`; until then, an external cron job is the simplest robust approach. The in-app "Export to TOML" remains useful for human-readable snapshots of the catalogue, but it does not capture audit history.

## Upgrades

Standard:

1. Build new image.
2. Stop container.
3. Start new container against the same volume.

Migrations run on startup. If a migration is destructive (drops a column, etc.), back up first. Migration testing in CI against a copy of the production DB is a good practice but not required for v1.

## Monitoring

The app exposes two operator endpoints (wired in `Program.cs`):

```csharp
app.MapHealthChecks("/healthz", new HealthCheckOptions {
    Predicate = check => check.Tags.Contains("healthz")
});
app.MapHealthChecks("/readyz", new HealthCheckOptions {
    Predicate = check => check.Tags.Contains("readyz")
});
```

- `/healthz` — liveness: `200` when the database is reachable **and** the Data Protection key ring round-trips; `503` otherwise. The Dockerfile `HEALTHCHECK` polls this.
- `/readyz` — readiness: only green once startup work (migrations + first-run seed + bootstrap admin) has finished. Gate reverse-proxy traffic on it.

## Logs

Standard ASP.NET Core logging to stdout. Whatever log aggregation the company uses (Loki, ELK, Datadog) picks them up via Docker logs.

Recommended log levels:

- `Microsoft`: Warning
- `Microsoft.EntityFrameworkCore`: Warning (Information if debugging migrations)
- `AlWorkspaceBuilder`: Information

## Resource sizing

This is a small tool. The app container needs ~256MB RAM and 0.5 CPU; the Postgres container at this load is comfortable in 512MB / 0.5 CPU. The expected steady state is dozens of templates per organisation and a few thousand audit log rows.

## TLS

The container should *not* terminate TLS itself. Run it behind a reverse proxy (Traefik, nginx, Caddy) that handles certificates. Set `app.UseForwardedHeaders()` to handle the `X-Forwarded-Proto` header so cookies get the `Secure` flag correctly.

## What's deliberately not here

- Multi-tenancy. There's one tenant: your team.
- Horizontal scaling. Blazor Server's SignalR connections are sticky to the server; one instance is enough for this load.
- A queue or worker process. Generation is synchronous and finishes in under a second.
