# syntax=docker/dockerfile:1.7

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/ALDevToolbox/ALDevToolbox.csproj src/ALDevToolbox/
RUN dotnet restore src/ALDevToolbox/ALDevToolbox.csproj

COPY . ./
RUN dotnet publish src/ALDevToolbox/ALDevToolbox.csproj -c Release -o /app /p:UseAppHost=false

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

COPY --from=build /app ./
COPY src/ALDevToolbox/Templates.seed ./Templates.seed

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    SEED_PATH=/app/Templates.seed \
    DB_PATH=/data/app.db

VOLUME ["/data"]

# /health/ready is the readiness probe: it pings SQLite via DbContextCheck.
# /health is liveness-only and would also satisfy this check but tells you
# less. The dotnet aspnet image already ships with curl.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl --fail --silent --show-error http://localhost:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "ALDevToolbox.dll"]
