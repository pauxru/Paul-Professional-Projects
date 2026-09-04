# UNVERIFIED: Docker is unavailable on the build host. This multi-stage file was authored only.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore Contoso.Storefront.slnx
RUN dotnet publish src/Contoso.Storefront.Api/Contoso.Storefront.Api.csproj -c Release -o /out/api --no-restore
RUN dotnet publish src/Contoso.Storefront.MigrationRunner/Contoso.Storefront.MigrationRunner.csproj -c Release -o /out/migrations --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /out/api .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Contoso.Storefront.Api.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS migrations
WORKDIR /app
COPY --from=build /out/migrations .
ENTRYPOINT ["dotnet", "Contoso.Storefront.MigrationRunner.dll"]
