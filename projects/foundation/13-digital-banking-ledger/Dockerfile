# syntax=docker/dockerfile:1
# ------------------------------------------------------------------------------
# Example Bank — Digital Banking Ledger — container image
#
# ⚠️  UNVERIFIED: Docker is not available on the build host, so this Dockerfile
#     and the accompanying docker-compose.yml were authored but never built or
#     run. They are provided to show intended packaging only.
# ------------------------------------------------------------------------------

# ---- Build stage -------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first for better layer caching.
COPY ExampleBank.Ledger.slnx ./
COPY src/ ./src/
COPY tests/ ./tests/
RUN dotnet restore ExampleBank.Ledger.slnx

# Build and publish the API as a self-contained framework-dependent app.
RUN dotnet publish src/ExampleBank.Ledger.Api/ExampleBank.Ledger.Api.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

# ---- Runtime stage -----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Run as a non-root user.
RUN adduser --disabled-password --gecos "" --uid 10001 ledger
USER ledger

COPY --from=build /app/publish ./

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:5013 \
    ConnectionStrings__Ledger="Data Source=/data/ledger.db"

EXPOSE 5013

# The SQLite file lives on a mounted volume so data survives container restarts.
VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD ["dotnet", "--info"]

ENTRYPOINT ["dotnet", "ExampleBank.Ledger.Api.dll"]
