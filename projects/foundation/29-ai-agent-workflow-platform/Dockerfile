# -----------------------------------------------------------------------------
# AI Agent Workflow Orchestration Platform — container image
#
# AUTHORED BUT NOT VERIFIED: Docker is unavailable on the build host, so this
# image has NOT been built or run. It is provided for completeness and review.
# -----------------------------------------------------------------------------

# --- Build stage -------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first for better layer caching.
COPY AgentPlatform.slnx Directory.Build.props ./
COPY src/ ./src/
COPY tests/ ./tests/
RUN dotnet restore AgentPlatform.slnx

# Build + publish the API in Release.
RUN dotnet publish src/AgentPlatform.Api/AgentPlatform.Api.csproj \
    -c Release -o /app/publish --no-restore

# --- Runtime stage -----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# Bind Kestrel to the platform's conventional port.
ENV ASPNETCORE_URLS=http://+:5029
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5029

# Run as the non-root user shipped in the base image.
USER $APP_UID

ENTRYPOINT ["dotnet", "AgentPlatform.Api.dll"]
