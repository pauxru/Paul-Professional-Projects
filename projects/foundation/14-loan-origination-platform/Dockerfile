# UNVERIFIED: Docker is unavailable on the build host.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore LoanOrigination.sln
RUN dotnet publish src/LoanOrigination.Api/LoanOrigination.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:5014
EXPOSE 5014
ENTRYPOINT ["dotnet", "LoanOrigination.Api.dll"]
