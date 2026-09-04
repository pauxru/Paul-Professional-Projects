# UNVERIFIED: Docker is unavailable on the build host.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore FieldOps.sln
RUN dotnet publish src/FieldOps.Api/FieldOps.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:5004
EXPOSE 5004
ENTRYPOINT ["dotnet", "FieldOps.Api.dll"]
