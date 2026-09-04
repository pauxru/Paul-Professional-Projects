# UNVERIFIED: Docker is unavailable on the build host.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore Northstar.Iga.sln
RUN dotnet publish src/Northstar.Iga.Api/Northstar.Iga.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:5024
EXPOSE 5024
ENTRYPOINT ["dotnet", "Northstar.Iga.Api.dll"]
