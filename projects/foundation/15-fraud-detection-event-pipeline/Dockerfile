# =============================================================================
# UNVERIFIED — This Dockerfile was authored but NOT built or run on this host.
# The host has no Docker installed. Fields are best-effort and MUST be
# verified end-to-end (build + run + smoke test) before use.
# See docker-compose.yml (also UNVERIFIED) for the intended local composition.
# =============================================================================

# ---- Build stage ------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution + project files first for restore cache efficiency
COPY *.slnx ./
COPY src/FraudPipeline.Domain/*.csproj                src/FraudPipeline.Domain/
COPY src/FraudPipeline.Application/*.csproj           src/FraudPipeline.Application/
COPY src/FraudPipeline.Infrastructure/*.csproj        src/FraudPipeline.Infrastructure/
COPY src/FraudPipeline.Api/*.csproj                   src/FraudPipeline.Api/
COPY tests/FraudPipeline.UnitTests/*.csproj           tests/FraudPipeline.UnitTests/
COPY tests/FraudPipeline.IntegrationTests/*.csproj    tests/FraudPipeline.IntegrationTests/
RUN dotnet restore FraudPipeline.slnx

# Copy the rest and publish
COPY . .
RUN dotnet publish src/FraudPipeline.Api/FraudPipeline.Api.csproj \
    -c Release -o /app/publish \
    /p:UseAppHost=false

# ---- Runtime stage ----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:5015
EXPOSE 5015

# Health check hits the /health/live endpoint added in Program.cs
HEALTHCHECK --interval=30s --timeout=5s --retries=3 CMD \
    wget --spider -q http://localhost:5015/health/live || exit 1

ENTRYPOINT ["dotnet", "FraudPipeline.Api.dll"]
