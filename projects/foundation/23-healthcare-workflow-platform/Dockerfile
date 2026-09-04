# UNVERIFIED — Docker is unavailable on the build host and the image has NOT been built or
# started. This file is provided as a starting point only.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Healthcare.slnx ./
COPY src/Healthcare.Domain/Healthcare.Domain.csproj src/Healthcare.Domain/
COPY src/Healthcare.Application/Healthcare.Application.csproj src/Healthcare.Application/
COPY src/Healthcare.Infrastructure/Healthcare.Infrastructure.csproj src/Healthcare.Infrastructure/
COPY src/Healthcare.Api/Healthcare.Api.csproj src/Healthcare.Api/
COPY tests/Healthcare.UnitTests/Healthcare.UnitTests.csproj tests/Healthcare.UnitTests/
COPY tests/Healthcare.IntegrationTests/Healthcare.IntegrationTests.csproj tests/Healthcare.IntegrationTests/
RUN dotnet restore Healthcare.slnx

COPY . .
RUN dotnet publish src/Healthcare.Api/Healthcare.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_URLS=http://+:5023
EXPOSE 5023
ENTRYPOINT ["dotnet", "Healthcare.Api.dll"]
