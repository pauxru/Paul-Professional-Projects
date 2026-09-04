# Dockerfile for the SampleApi (target under test). Multi-stage build.
#
# THIS FILE HAS NOT BEEN VERIFIED — Docker is not installed on the build host.
# See docker-compose.yml for the accompanying compose stack (also UNVERIFIED).

# ---- Build ---------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY LoadRunner.slnx ./
COPY src/ src/
COPY tests/ tests/
RUN dotnet restore src/SampleApi/SampleApi.csproj
RUN dotnet publish src/SampleApi/SampleApi.csproj -c Release -o /out/sampleapi \
    --no-restore /p:UseAppHost=false

# ---- Runtime -------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out/sampleapi ./
EXPOSE 5027
ENV ASPNETCORE_URLS=http://+:5027
ENV ASPNETCORE_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "SampleApi.dll"]
