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

COPY --from=build /app ./
COPY ALDevToolbox/Templates.seed ./Templates.seed

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    SEED_PATH=/app/Templates.seed

# /health/ready is the readiness probe: it pings Postgres via DbContextCheck.
# /health is liveness-only and would also satisfy this check but tells you
# less. The dotnet aspnet image already ships with curl.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl --fail --silent --show-error http://localhost:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "ALDevToolbox.dll"]
