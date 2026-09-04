# syntax=docker/dockerfile:1
# ============================================================================
# UNVERIFIED — this Dockerfile has NOT been built or executed in this
# environment. The current workflow targets Windows + .NET SDK 10.0.400 with
# no Docker daemon available. Included for portfolio completeness and as a
# starting point for a future containerised deployment.
# ============================================================================

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY AuditPlatform.sln ./
COPY src/AuditPlatform.Domain/AuditPlatform.Domain.csproj src/AuditPlatform.Domain/
COPY src/AuditPlatform.Application/AuditPlatform.Application.csproj src/AuditPlatform.Application/
COPY src/AuditPlatform.Infrastructure/AuditPlatform.Infrastructure.csproj src/AuditPlatform.Infrastructure/
COPY src/AuditPlatform.Api/AuditPlatform.Api.csproj src/AuditPlatform.Api/
COPY tests/AuditPlatform.UnitTests/AuditPlatform.UnitTests.csproj tests/AuditPlatform.UnitTests/
COPY tests/AuditPlatform.IntegrationTests/AuditPlatform.IntegrationTests.csproj tests/AuditPlatform.IntegrationTests/
RUN dotnet restore

COPY . .
RUN dotnet publish src/AuditPlatform.Api/AuditPlatform.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
EXPOSE 5019
ENV ASPNETCORE_URLS=http://+:5019
ENV ASPNETCORE_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "AuditPlatform.Api.dll"]
