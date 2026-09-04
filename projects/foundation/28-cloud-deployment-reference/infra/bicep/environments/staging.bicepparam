using '../main.bicep'

param environment = 'staging'
param location = 'eastus2'
param namePrefix = 'contoso-storefront'
param tags = {
  application: 'contoso-retail-storefront'
  environment: 'staging'
  owner: 'platform-engineering'
  'managed-by': 'bicep'
  'data-classification': 'internal-demo'
}
param containerMinReplicas = 1
param containerMaxReplicas = 5
param postgresSkuName = 'Standard_D2s_v3'
param postgresStorageGb = 64
param postgresZoneRedundant = false
param redisSkuName = 'Standard'
param redisFamily = 'C'
param redisCapacity = 1
param serviceBusSku = 'Standard'
param usePrivateEndpoints = true
param imageTag = 'latest'
param postgresAdministratorPassword = readEnvironmentVariable('POSTGRES_ADMIN_PASSWORD')
param jwtSigningKey = readEnvironmentVariable('JWT_SIGNING_KEY')
