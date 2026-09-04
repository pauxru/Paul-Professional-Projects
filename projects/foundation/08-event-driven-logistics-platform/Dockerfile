# UNVERIFIED: Docker is unavailable on the build host; this image has not been built or run.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore SavannaLogistics.sln
RUN dotnet publish src/SavannaLogistics.Api/SavannaLogistics.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:5008
EXPOSE 5008
ENTRYPOINT ["dotnet", "SavannaLogistics.Api.dll"]
