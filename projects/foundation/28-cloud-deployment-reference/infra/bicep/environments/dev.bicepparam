using '../main.bicep'

param environment = 'dev'
param location = 'eastus2'
param namePrefix = 'contoso-storefront'
param tags = {
  application: 'contoso-retail-storefront'
  environment: 'dev'
  owner: 'platform-engineering'
  'managed-by': 'bicep'
  'data-classification': 'internal-demo'
}
param containerMinReplicas = 1
param containerMaxReplicas = 3
param postgresSkuName = 'B_Standard_B1ms'
param postgresStorageGb = 32
param postgresZoneRedundant = false
param redisSkuName = 'Basic'
param redisFamily = 'C'
param redisCapacity = 0
param serviceBusSku = 'Standard'
param usePrivateEndpoints = false
param imageTag = 'latest'
param postgresAdministratorPassword = readEnvironmentVariable('POSTGRES_ADMIN_PASSWORD')
param jwtSigningKey = readEnvironmentVariable('JWT_SIGNING_KEY')
