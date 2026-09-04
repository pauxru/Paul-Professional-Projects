# syntax=docker/dockerfile:1
# -------------------------------------------------------------------------------------------------
# UNVERIFIED: Docker is not available on the build host. This Dockerfile was authored but has NOT
# been built or run. It is provided for completeness only. The application builds, tests and runs
# with only the .NET SDK — no container is required.
# -------------------------------------------------------------------------------------------------

# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first for layer caching
COPY Idp.slnx ./
COPY src/ ./src/
COPY tests/ ./tests/
RUN dotnet restore Idp.slnx

# Build and publish the API
RUN dotnet publish src/Idp.Api/Idp.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# The API listens on 5009 (see ASPNETCORE_URLS)
ENV ASPNETCORE_URLS=http://+:5009
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5009

# A writable location for the SQLite database and artifact folders
VOLUME ["/app/data"]
ENV Database__ConnectionString="Data Source=/app/data/idp.db"
ENV Storage__RootPath="/app/data/object-store"
ENV Export__OutboxPath="/app/data/outbox"
ENV Export__DeadLetterPath="/app/data/deadletter"

# NOTE: set a real Jwt__SigningKey at runtime; the app refuses to start in Production with the
# default development key.

ENTRYPOINT ["dotnet", "Idp.Api.dll"]
