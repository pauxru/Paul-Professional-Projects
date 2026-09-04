# syntax=docker/dockerfile:1
# -----------------------------------------------------------------------------
# UNVERIFIED: Docker is unavailable on the build host. This Dockerfile was
# authored but has NOT been built or run. Treat as illustrative.
# -----------------------------------------------------------------------------
# Multi-stage build producing one image that can run EITHER the API or a worker.
# The API is the default entrypoint; worker services override the command in
# docker-compose.yml (dotnet /app/worker/JobScheduler.Worker.dll --node-id wN).

# ---- Build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (better layer caching)
COPY JobScheduler.slnx ./
COPY src/ ./src/
COPY tests/ ./tests/
RUN dotnet restore JobScheduler.slnx

# Publish the API and the Worker
RUN dotnet publish src/JobScheduler.Api/JobScheduler.Api.csproj    -c Release -o /app/api    --no-restore
RUN dotnet publish src/JobScheduler.Worker/JobScheduler.Worker.csproj -c Release -o /app/worker --no-restore

# ---- Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/api    ./api
COPY --from=build /app/worker ./worker

# Shared SQLite database location (mount a volume here so nodes coordinate).
ENV Database__ConnectionString="Data Source=/data/jobscheduler.db;Cache=Shared"
ENV ASPNETCORE_URLS="http://+:5017"
EXPOSE 5017
VOLUME ["/data"]

# Default: run the API. Workers override this command.
ENTRYPOINT ["dotnet", "/app/api/JobScheduler.Api.dll"]
