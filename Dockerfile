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
# ships it. postgresql-client-18 supplies pg_dump / pg_restore, which the M18
# BackupService shells out to. pg_dump refuses to dump a server newer than
# itself, so the client major must match the compose db image (postgres:18).
# Debian's default postgresql-client is older than 18, so we install from the
# PGDG apt repo to keep client and server in lockstep.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates gnupg \
    && install -d /usr/share/postgresql-common/pgdg \
    && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
        -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc \
    && echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt $(. /etc/os-release && echo $VERSION_CODENAME)-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client-18 \
    && apt-get purge -y --auto-remove gnupg \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

# /healthz exercises both the Postgres connection and the Data Protection key
# ring; /readyz only flips green once startup work (migrations + bootstrap
# admin) has finished. The container HEALTHCHECK is liveness-oriented, so it
# polls /healthz — a node that loses Postgres or its DP keys should drop out
# of rotation regardless of startup state.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl --fail --silent --show-error http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "ALDevToolbox.dll"]
