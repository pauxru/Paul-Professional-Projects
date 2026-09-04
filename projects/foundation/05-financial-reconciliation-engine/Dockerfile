# ---------------------------------------------------------------------------
# Dockerfile for ReconEngine.Api  (AUTHORED, NOT VERIFIED)
#
# Docker is NOT installed on the build host, so this image has never been built
# or run. It is provided for completeness and is expected to work but is
# UNVERIFIED. The application needs no containers to run locally: `dotnet run`
# with the default SQLite database is sufficient.
# ---------------------------------------------------------------------------

# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (better layer caching)
COPY Directory.Build.props ./
COPY ReconEngine.slnx ./
COPY src/ ./src/
COPY tests/ ./tests/
RUN dotnet restore src/ReconEngine.Api/ReconEngine.Api.csproj

# Build + publish the API
RUN dotnet publish src/ReconEngine.Api/ReconEngine.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# Non-root user
RUN useradd --uid 10001 --create-home appuser \
    && chown -R appuser:appuser /app
USER appuser

ENV ASPNETCORE_URLS=http://+:5005 \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__Default="Data Source=/app/data/recon.db"

EXPOSE 5005

# A writable location for the SQLite file
VOLUME ["/app/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD ["/bin/sh", "-c", "wget -qO- http://localhost:5005/health/ready || exit 1"]

ENTRYPOINT ["dotnet", "ReconEngine.Api.dll"]
