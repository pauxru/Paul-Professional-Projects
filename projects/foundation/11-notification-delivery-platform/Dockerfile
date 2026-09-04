# UNVERIFIED — Docker is unavailable on the build host.  This Dockerfile
# has never been built or run.  It represents the intended containerisation
# and is provided for completeness.  Do not treat it as validated.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY NotificationPlatform.slnx ./
COPY src/ ./src/
COPY tests/ ./tests/
RUN dotnet restore
RUN dotnet publish src/NotificationPlatform.Api/NotificationPlatform.Api.csproj \
    -c Release -o /out --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://+:5011
EXPOSE 5011
ENTRYPOINT ["dotnet", "NotificationPlatform.Api.dll"]
