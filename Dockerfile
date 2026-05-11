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
# ships it. postgresql-client supplies pg_dump / pg_restore, which the M18
# BackupService shells out to. The Debian default major may differ from the
# compose db image (postgres:18) — operators who run scheduled backups in
# production should override this RUN with postgresql-client-18 from the
# pgdg repo to match. Keep the install lean so the image size doesn't drift.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl postgresql-client \
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
