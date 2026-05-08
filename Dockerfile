# syntax=docker/dockerfile:1.7

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ALDevToolbox.csproj ./
RUN dotnet restore ALDevToolbox.csproj

COPY . ./
RUN dotnet publish ALDevToolbox.csproj -c Release -o /app /p:UseAppHost=false

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

COPY --from=build /app ./
COPY Templates.seed ./Templates.seed

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    SEED_PATH=/app/Templates.seed \
    DB_PATH=/data/app.db

VOLUME ["/data"]

ENTRYPOINT ["dotnet", "ALDevToolbox.dll"]
