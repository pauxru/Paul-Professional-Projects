# =====================================================================
# UNVERIFIED. This Dockerfile is provided as an intent document only.
# Docker is not available on the build host; the image below has not
# been built or run. Treat this as a template for a real deployment
# and expect to iterate on it in your own CI once you validate the image.
# =====================================================================

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj files first for layer caching
COPY ["src/ZeroTrust.Domain/ZeroTrust.Domain.csproj", "src/ZeroTrust.Domain/"]
COPY ["src/ZeroTrust.Application/ZeroTrust.Application.csproj", "src/ZeroTrust.Application/"]
COPY ["src/ZeroTrust.Infrastructure/ZeroTrust.Infrastructure.csproj", "src/ZeroTrust.Infrastructure/"]
COPY ["src/ZeroTrust.Api/ZeroTrust.Api.csproj", "src/ZeroTrust.Api/"]
RUN dotnet restore "src/ZeroTrust.Api/ZeroTrust.Api.csproj"

# Copy the rest and publish
COPY . .
RUN dotnet publish "src/ZeroTrust.Api/ZeroTrust.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Non-root user
RUN useradd -m -u 10001 ntsf && chown -R ntsf /app
USER ntsf

ENV ASPNETCORE_URLS=http://+:5007
EXPOSE 5007

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD wget -qO- http://localhost:5007/health/ready || exit 1

ENTRYPOINT ["dotnet", "ZeroTrust.Api.dll"]
