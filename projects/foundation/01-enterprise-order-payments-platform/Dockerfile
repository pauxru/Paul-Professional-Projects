# syntax=docker/dockerfile:1
# UNVERIFIED: this Dockerfile is committed to demonstrate intent but has never been built or
# run on the host that produced this repository (Docker is unavailable on the build host).
# Once Docker is available, `docker build -t contoso-payments:local .` should produce a
# working image.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Contoso.Payments.slnx ./
COPY Directory.Build.props ./
COPY src/Contoso.Payments.Domain/ ./src/Contoso.Payments.Domain/
COPY src/Contoso.Payments.Application/ ./src/Contoso.Payments.Application/
COPY src/Contoso.Payments.Infrastructure/ ./src/Contoso.Payments.Infrastructure/
COPY src/Contoso.Payments.Api/ ./src/Contoso.Payments.Api/

RUN dotnet restore Contoso.Payments.slnx
RUN dotnet publish src/Contoso.Payments.Api/Contoso.Payments.Api.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:5001 \
    ASPNETCORE_ENVIRONMENT=Production

EXPOSE 5001
ENTRYPOINT ["dotnet", "Contoso.Payments.Api.dll"]
