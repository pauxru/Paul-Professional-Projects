# UNVERIFIED: Docker is unavailable on the build host.
# This Dockerfile is authored for deployment review and has not been built on this host.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore FeatureFlags.sln
RUN dotnet publish src/FeatureFlags.Api/FeatureFlags.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:5018
ENV Database__ConnectionString="Data Source=/data/featureflags.db"
COPY --from=build /out .
EXPOSE 5018
ENTRYPOINT ["dotnet", "FeatureFlags.Api.dll"]
