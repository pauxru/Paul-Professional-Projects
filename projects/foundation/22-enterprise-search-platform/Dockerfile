# UNVERIFIED: Docker is unavailable on the build host.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore EnterpriseSearch.sln
RUN dotnet publish src/EnterpriseSearch.Api/EnterpriseSearch.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
EXPOSE 5022
ENV ASPNETCORE_URLS=http://+:5022
ENTRYPOINT ["dotnet", "EnterpriseSearch.Api.dll"]
