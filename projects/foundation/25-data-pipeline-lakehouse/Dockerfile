# UNVERIFIED: Docker is not available on the build host. This Dockerfile was authored for completeness
# and has NOT been built or run. See README "Running with Docker".
#
# Multi-stage build for the Lakehouse API (.NET 10).

# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# restore (copy csproj/slnx first for layer caching)
COPY Lakehouse.slnx ./
COPY src/ ./src/
COPY tests/ ./tests/
RUN dotnet restore Lakehouse.slnx

# publish the API
RUN dotnet publish src/Lakehouse.Api/Lakehouse.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# the API listens on 5025 (see appsettings "Urls" / ASPNETCORE_URLS)
ENV ASPNETCORE_URLS=http://+:5025
ENV Lakehouse__LakeRoot=/data/lake
ENV Lakehouse__ServingDbPath=/data/serving/serving.db
EXPOSE 5025

# persist the lake + serving db outside the container
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "Lakehouse.Api.dll"]
