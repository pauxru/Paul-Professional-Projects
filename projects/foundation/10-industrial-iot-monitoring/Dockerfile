# UNVERIFIED: Docker is unavailable on the build host.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore IndustrialIotMonitoring.slnx
RUN dotnet publish src/Iiot.Api/Iiot.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:5010
EXPOSE 5010 18830
ENTRYPOINT ["dotnet", "Iiot.Api.dll"]
