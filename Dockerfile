# syntax=docker/dockerfile:1.7

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ALDevToolbox/ALDevToolbox.csproj ALDevToolbox/
RUN dotnet restore ALDevToolbox/ALDevToolbox.csproj

COPY . ./
RUN dotnet publish ALDevToolbox/ALDevToolbox.csproj -c Release -o /app /p:UseAppHost=false

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# curl is needed for the HEALTHCHECK below; the slim aspnet image no longer
# ships it. postgresql-client-18 carries pg_dump / pg_restore, which the M18
# BackupService shells out to — the major version must match the db image
# (compose pins postgres:18), so install from the upstream PostgreSQL repo
# rather than the Debian default. Keep the install lean so the image size
# doesn't drift up.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates gnupg lsb-release \
    && install -d /usr/share/keyrings \
    && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
       | gpg --dearmor -o /usr/share/keyrings/postgresql.gpg \
    && echo "deb [signed-by=/usr/share/keyrings/postgresql.gpg] http://apt.postgresql.org/pub/repos/apt $(lsb_release -cs)-pgdg main" \
       > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client-18 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./
COPY ALDevToolbox/Templates.seed ./Templates.seed

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    SEED_PATH=/app/Templates.seed

# /health/ready is the readiness probe: it pings Postgres via DbContextCheck.
# /health is liveness-only and would also satisfy this check but tells you
# less.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl --fail --silent --show-error http://localhost:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "ALDevToolbox.dll"]
