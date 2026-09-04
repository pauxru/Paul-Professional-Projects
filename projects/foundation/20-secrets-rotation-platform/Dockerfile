# UNVERIFIED: Docker is unavailable on the build host.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore Northstar.SecretsRotation.sln
RUN dotnet publish src/Northstar.Secrets.Api/Northstar.Secrets.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:5020
EXPOSE 5020
ENTRYPOINT ["dotnet", "Northstar.Secrets.Api.dll"]
