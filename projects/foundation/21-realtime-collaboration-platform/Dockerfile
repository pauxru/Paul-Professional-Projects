# syntax=docker/dockerfile:1
# ------------------------------------------------------------------------------------------------
# UNVERIFIED: Docker is not available on the build host, so this image has NOT been built or run.
# It is authored to a standard, correct pattern for an ASP.NET Core 10 app. Treat as a starting
# point, not a proven artifact.
# ------------------------------------------------------------------------------------------------

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first for better layer caching.
COPY Collab.slnx ./
COPY src/Collab.Domain/Collab.Domain.csproj                 src/Collab.Domain/
COPY src/Collab.Application/Collab.Application.csproj        src/Collab.Application/
COPY src/Collab.Infrastructure/Collab.Infrastructure.csproj src/Collab.Infrastructure/
COPY src/Collab.Api/Collab.Api.csproj                       src/Collab.Api/
RUN dotnet restore src/Collab.Api/Collab.Api.csproj

# Build + publish.
COPY . .
RUN dotnet publish src/Collab.Api/Collab.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# The app serves HTTP on 5021 (no in-container HTTPS; terminate TLS at the edge).
ENV ASPNETCORE_URLS=http://+:5021
EXPOSE 5021

# SQLite database file lives in the working directory by default; mount a volume to persist it.
ENTRYPOINT ["dotnet", "Collab.Api.dll"]
