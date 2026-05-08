# Deployment

## Target environment

The app runs as a single Docker container behind whatever ingress the company uses (Traefik, nginx, etc.). One container, one volume, one set of env vars.

## Dockerfile

A multi-stage build producing a self-contained image:

```dockerfile
# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY *.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o /app /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app ./
COPY Templates.seed ./Templates.seed
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV SEED_PATH=/app/Templates.seed
ENV DB_PATH=/data/app.db
ENTRYPOINT ["dotnet", "AlWorkspaceBuilder.dll"]
```

Key points:

- `Templates.seed/` is copied into the image. The seed files are part of the build artifact, not provided at runtime.
- `/data` is the directory the database lives in. It's expected to be a mounted volume.
- Port 8080 inside the container, mapped however the host wants.

## docker-compose example

```yaml
services:
  alwb:
    image: alwb:latest
    ports:
      - "8080:8080"
    volumes:
      - ./data:/data
    environment:
      ADMIN_PASSWORD_FILE: /run/secrets/admin_password
      DB_PATH: /data/app.db
      SEED_PATH: /app/Templates.seed
    secrets:
      - admin_password
    restart: unless-stopped

secrets:
  admin_password:
    file: ./secrets/admin_password.txt
```

The password is read from a file rather than directly from an environment variable so it doesn't end up in `docker inspect` output. The app should support both `ADMIN_PASSWORD` (for local dev) and `ADMIN_PASSWORD_FILE` (for production), preferring the file if both are set.

## Environment variables

| Variable                  | Purpose                                             | Default                  |
|---------------------------|-----------------------------------------------------|--------------------------|
| `ADMIN_PASSWORD`          | The shared admin password (dev / quick start)       | none — required          |
| `ADMIN_PASSWORD_FILE`     | Path to a file containing the password (production) | none                     |
| `DB_PATH`                 | Path to the SQLite file                             | `/data/app.db`           |
| `SEED_PATH`               | Path to the `Templates.seed/` directory             | `/app/Templates.seed`    |
| `ASPNETCORE_URLS`         | Standard ASP.NET Core binding                       | `http://+:8080`          |
| `ASPNETCORE_ENVIRONMENT`  | Standard ASP.NET Core environment                   | `Production`             |

If `ADMIN_PASSWORD` and `ADMIN_PASSWORD_FILE` are both unset, the app should fail to start with a clear error message. Don't silently default to a weak password.

## First-run behaviour

On first start against an empty `/data` directory:

1. The app creates `app.db` at `DB_PATH`.
2. EF Core migrations run, creating all tables.
3. `SeedService` runs — reads `Templates.seed/` and populates the tables.
4. The app starts serving requests.

On subsequent starts, the app:

1. Confirms the schema matches (runs any pending migrations).
2. Skips seeding (database is non-empty).
3. Starts serving.

## Backups

The entire state of the app is in two places:

- The SQLite file at `DB_PATH`. Back this up.
- The seed files in the image. Already version-controlled.

Backup is `cp /data/app.db /backups/app.db.$(date +%Y%m%d)`. Or take a volume snapshot. Or use the in-app "Export to TOML" feature periodically and commit the result.

For point-in-time recovery: SQLite supports online backup via the backup API. A small sidecar process running `sqlite3 /data/app.db ".backup /backups/app.db.bak"` on a cron schedule is the simplest robust approach.

## Upgrades

Standard:

1. Build new image.
2. Stop container.
3. Start new container against the same volume.

Migrations run on startup. If a migration is destructive (drops a column, etc.), back up first. Migration testing in CI against a copy of the production DB is a good practice but not required for v1.

## Monitoring

Use the framework's standard health check endpoints:

```csharp
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready", new HealthCheckOptions {
    Predicate = check => check.Tags.Contains("ready")
});
```

Register a health check that pings the database. That covers "is the app up and can it talk to its data."

## Logs

Standard ASP.NET Core logging to stdout. Whatever log aggregation the company uses (Loki, ELK, Datadog) picks them up via Docker logs.

Recommended log levels:

- `Microsoft`: Warning
- `Microsoft.EntityFrameworkCore`: Warning (Information if debugging migrations)
- `AlWorkspaceBuilder`: Information

## Resource sizing

This is a small tool. 256MB RAM, 0.5 CPU is generous. SQLite scales fine for this — a database with thousands of templates would still respond in milliseconds. The expected steady state is dozens of templates and a few hundred audit log rows.

## TLS

The container should *not* terminate TLS itself. Run it behind a reverse proxy (Traefik, nginx, Caddy) that handles certificates. Set `app.UseForwardedHeaders()` to handle the `X-Forwarded-Proto` header so cookies get the `Secure` flag correctly.

## What's deliberately not here

- Multi-tenancy. There's one tenant: your team.
- Horizontal scaling. Blazor Server's SignalR connections are sticky to the server; one instance is enough for this load.
- A separate database service. SQLite is the database.
- A queue or worker process. Generation is synchronous and finishes in under a second.
