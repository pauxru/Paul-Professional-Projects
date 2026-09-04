# UNVERIFIED: Docker is unavailable on the build host.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore SubscriptionBilling.sln
RUN dotnet publish src/SubscriptionBilling.Api/SubscriptionBilling.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:5016
EXPOSE 5016
ENTRYPOINT ["dotnet", "SubscriptionBilling.Api.dll"]
