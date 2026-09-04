param namePrefix string
param location string
param tags object
param enablePurgeProtection bool
param publicNetworkAccess string

var vaultName = take(toLower(replace('kv-${namePrefix}', '-', '')), 24)

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: vaultName
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enablePurgeProtection: enablePurgeProtection
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    publicNetworkAccess: publicNetworkAccess
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: publicNetworkAccess == 'Disabled' ? 'Deny' : 'Allow'
    }
  }
}

output id string = vault.id
output name string = vault.name
output vaultUri string = vault.properties.vaultUri
