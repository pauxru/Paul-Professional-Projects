# UNVERIFIED: Docker is unavailable on the build host.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Lab.SampleApp/Lab.SampleApp.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:5006
EXPOSE 5006
ENTRYPOINT ["dotnet", "Lab.SampleApp.dll"]
