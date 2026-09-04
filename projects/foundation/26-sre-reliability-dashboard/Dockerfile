# UNVERIFIED: Docker is unavailable on the build host.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore Northstar.Reliability.sln
RUN dotnet publish src/Northstar.Reliability.Api/Northstar.Reliability.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:5026
ENV ASPNETCORE_ENVIRONMENT=Development
EXPOSE 5026
ENTRYPOINT ["dotnet", "Northstar.Reliability.Api.dll"]
