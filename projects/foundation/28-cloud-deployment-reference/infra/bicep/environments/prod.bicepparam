using '../main.bicep'

param environment = 'prod'
param location = 'eastus2'
param namePrefix = 'contoso-storefront'
param tags = {
  application: 'contoso-retail-storefront'
  environment: 'prod'
  owner: 'platform-engineering'
  'managed-by': 'bicep'
  'data-classification': 'confidential'
}
param containerMinReplicas = 3
param containerMaxReplicas = 20
param postgresSkuName = 'Standard_D4s_v3'
param postgresStorageGb = 256
param postgresZoneRedundant = true
param redisSkuName = 'Premium'
param redisFamily = 'P'
param redisCapacity = 1
param serviceBusSku = 'Premium'
param usePrivateEndpoints = true
param imageTag = 'latest'
param postgresAdministratorPassword = readEnvironmentVariable('POSTGRES_ADMIN_PASSWORD')
param jwtSigningKey = readEnvironmentVariable('JWT_SIGNING_KEY')
