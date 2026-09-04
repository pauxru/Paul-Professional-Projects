# UNVERIFIED — this Dockerfile is shipped as a reference artefact for the
# production adapter path. It has NOT been built or run in CI. See ADR 0003
# and docs/portfolio/checklist.md.
#
# Build:  docker build -t rag-assistant:local -f Dockerfile .
# Run:    docker run --rm -p 5003:5003 rag-assistant:local

# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY *.sln ./
COPY src/RagAssistant.Domain/RagAssistant.Domain.csproj src/RagAssistant.Domain/
COPY src/RagAssistant.Application/RagAssistant.Application.csproj src/RagAssistant.Application/
COPY src/RagAssistant.Infrastructure/RagAssistant.Infrastructure.csproj src/RagAssistant.Infrastructure/
COPY src/RagAssistant.Api/RagAssistant.Api.csproj src/RagAssistant.Api/
COPY src/RagAssistant.Eval/RagAssistant.Eval.csproj src/RagAssistant.Eval/
COPY tests/RagAssistant.UnitTests/RagAssistant.UnitTests.csproj tests/RagAssistant.UnitTests/
COPY tests/RagAssistant.IntegrationTests/RagAssistant.IntegrationTests.csproj tests/RagAssistant.IntegrationTests/
RUN dotnet restore

COPY . .
RUN dotnet publish src/RagAssistant.Api -c Release -o /app/publish --no-restore

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:5003
EXPOSE 5003

# Non-root user recommended in production
RUN useradd --create-home --shell /bin/bash rag
USER rag

ENTRYPOINT ["dotnet", "RagAssistant.Api.dll"]
