# UNVERIFIED: Docker is unavailable on the build host.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore IntegrationHub.sln
RUN dotnet publish src/IntegrationHub.Api/IntegrationHub.Api.csproj -c Release -o /out/api --no-restore
RUN dotnet publish src/IntegrationHub.Simulators.Crm/IntegrationHub.Simulators.Crm.csproj -c Release -o /out/crm --no-restore
RUN dotnet publish src/IntegrationHub.Simulators.Erp/IntegrationHub.Simulators.Erp.csproj -c Release -o /out/erp --no-restore
RUN dotnet publish src/IntegrationHub.Simulators.Payments/IntegrationHub.Simulators.Payments.csproj -c Release -o /out/payments --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
LABEL org.opencontainers.image.description="UNVERIFIED API Integration Hub image"
WORKDIR /app
COPY --from=build /out/api .
ENV ASPNETCORE_URLS=http://+:5012
EXPOSE 5012
ENTRYPOINT ["dotnet", "IntegrationHub.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS crm
LABEL org.opencontainers.image.description="UNVERIFIED Contoso CRM simulator image"
WORKDIR /app
COPY --from=build /out/crm .
ENV ASPNETCORE_URLS=http://+:5112
EXPOSE 5112
ENTRYPOINT ["dotnet", "IntegrationHub.Simulators.Crm.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS erp
LABEL org.opencontainers.image.description="UNVERIFIED Acme ERP simulator image"
WORKDIR /app
COPY --from=build /out/erp .
ENV ASPNETCORE_URLS=http://+:5212
EXPOSE 5212
ENTRYPOINT ["dotnet", "IntegrationHub.Simulators.Erp.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS payments
LABEL org.opencontainers.image.description="UNVERIFIED PesaGate simulator image"
WORKDIR /app
COPY --from=build /out/payments .
ENV ASPNETCORE_URLS=http://+:5312
EXPOSE 5312
ENTRYPOINT ["dotnet", "IntegrationHub.Simulators.Payments.dll"]
