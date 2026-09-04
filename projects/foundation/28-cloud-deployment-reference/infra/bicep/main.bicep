targetScope = 'subscription'

@allowed([
  'dev'
  'staging'
  'prod'
])
param environment string
param location string = 'eastus2'
@minLength(5)
param namePrefix string = 'contoso-storefront'
param tags object
param containerMinReplicas int
param containerMaxReplicas int
param postgresSkuName string
param postgresStorageGb int
param postgresZoneRedundant bool
param redisSkuName string
param redisFamily string
param redisCapacity int
param serviceBusSku string
param usePrivateEndpoints bool
param imageTag string = 'latest'
@secure()
param postgresAdministratorPassword string
@secure()
param jwtSigningKey string

var suffix = '${namePrefix}-${environment}'

resource deploymentResourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-${suffix}'
  location: location
  tags: tags
}

module network 'modules/network.bicep' = {
  name: 'network-${environment}'
  scope: deploymentResourceGroup
  params: {
    namePrefix: suffix
    location: location
    tags: tags
  }
}

module observability 'modules/observability.bicep' = {
  name: 'observability-${environment}'
  scope: deploymentResourceGroup
  params: {
    namePrefix: suffix
    location: location
    tags: tags
    retentionDays: environment == 'prod' ? 90 : 30
  }
}

module registry 'modules/registry.bicep' = {
  name: 'registry-${environment}'
  scope: deploymentResourceGroup
  params: {
    namePrefix: suffix
    location: location
    tags: tags
    skuName: environment == 'prod' ? 'Premium' : 'Basic'
  }
}

module identity 'modules/identity.bicep' = {
  name: 'identity-${environment}'
  scope: deploymentResourceGroup
  params: {
    namePrefix: suffix
    location: location
    tags: tags
  }
}

module keyVault 'modules/key-vault.bicep' = {
  name: 'keyvault-${environment}'
  scope: deploymentResourceGroup
  params: {
    namePrefix: suffix
    location: location
    tags: tags
    enablePurgeProtection: environment == 'prod'
    publicNetworkAccess: usePrivateEndpoints ? 'Disabled' : 'Enabled'
  }
}

module postgres 'modules/postgres.bicep' = {
  name: 'postgres-${environment}'
  scope: deploymentResourceGroup
  params: {
    namePrefix: suffix
    location: location
    tags: tags
    administratorLogin: 'storefrontadmin'
    administratorPassword: postgresAdministratorPassword
    skuName: postgresSkuName
    storageGb: postgresStorageGb
    zoneRedundant: postgresZoneRedundant
    delegatedSubnetId: usePrivateEndpoints ? network.outputs.databaseSubnetId : ''
    privateDnsZoneId: usePrivateEndpoints ? network.outputs.postgresPrivateDnsZoneId : ''
    publicNetworkAccess: usePrivateEndpoints ? 'Disabled' : 'Enabled'
  }
}

module redis 'modules/redis.bicep' = {
  name: 'redis-${environment}'
  scope: deploymentResourceGroup
  params: {
    namePrefix: suffix
    location: location
    tags: tags
    skuName: redisSkuName
    family: redisFamily
    capacity: redisCapacity
    publicNetworkAccess: usePrivateEndpoints ? 'Disabled' : 'Enabled'
  }
}

module messaging 'modules/service-bus.bicep' = {
  name: 'servicebus-${environment}'
  scope: deploymentResourceGroup
  params: {
    namePrefix: suffix
    location: location
    tags: tags
    skuName: serviceBusSku
    publicNetworkAccess: usePrivateEndpoints ? 'Disabled' : 'Enabled'
  }
}

module rbac 'modules/rbac.bicep' = {
  name: 'rbac-${environment}'
  scope: deploymentResourceGroup
  params: {
    principalId: identity.outputs.principalId
    keyVaultName: keyVault.outputs.name
    registryName: registry.outputs.name
    serviceBusNamespaceName: messaging.outputs.namespaceName
  }
}

module secrets 'modules/key-vault-secrets.bicep' = {
  name: 'keyvault-secrets-${environment}'
  scope: deploymentResourceGroup
  params: {
    keyVaultName: keyVault.outputs.name
    databaseConnectionString: 'Host=${postgres.outputs.host};Port=5432;Database=storefront;Username=storefrontadmin;Password=${postgresAdministratorPassword};SSL Mode=Require;Trust Server Certificate=false'
    redisConnectionString: redis.outputs.connectionString
    applicationInsightsConnectionString: observability.outputs.applicationInsightsConnectionString
    jwtSigningKey: jwtSigningKey
  }
}

module privateEndpoints 'modules/private-endpoints.bicep' = {
  name: 'private-endpoints-${environment}'
  scope: deploymentResourceGroup
  params: {
    enabled: usePrivateEndpoints
    namePrefix: suffix
    location: location
    tags: tags
    subnetId: network.outputs.privateEndpointSubnetId
    keyVaultId: keyVault.outputs.id
    registryId: registry.outputs.id
    redisId: redis.outputs.id
    serviceBusId: messaging.outputs.namespaceId
  }
}

module containerApps 'modules/container-apps.bicep' = {
  name: 'container-apps-${environment}'
  scope: deploymentResourceGroup
  params: {
    namePrefix: suffix
    location: location
    tags: tags
    logAnalyticsCustomerId: observability.outputs.customerId
    logAnalyticsSharedKey: observability.outputs.sharedKey
    infrastructureSubnetId: usePrivateEndpoints ? network.outputs.containerAppsSubnetId : ''
    registryServer: registry.outputs.loginServer
    identityId: identity.outputs.id
    keyVaultUri: keyVault.outputs.vaultUri
    imageTag: imageTag
    minReplicas: containerMinReplicas
    maxReplicas: containerMaxReplicas
    environmentName: environment
    postgresHost: postgres.outputs.host
    redisHost: redis.outputs.host
    serviceBusNamespace: messaging.outputs.fullyQualifiedNamespace
  }
  dependsOn: [
    rbac
    secrets
    privateEndpoints
  ]
}

output resourceGroupName string = deploymentResourceGroup.name
output apiFqdn string = containerApps.outputs.apiFqdn
output registryLoginServer string = registry.outputs.loginServer
output keyVaultUri string = keyVault.outputs.vaultUri
