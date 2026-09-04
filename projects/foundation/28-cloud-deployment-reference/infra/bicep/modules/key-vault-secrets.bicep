param keyVaultName string
@secure()
param databaseConnectionString string
@secure()
param redisConnectionString string
@secure()
param applicationInsightsConnectionString string
@secure()
param jwtSigningKey string

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource databaseSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'storefront--database--connectionstring'
  properties: {
    value: databaseConnectionString
  }
}

resource redisSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'storefront--cache--connectionstring'
  properties: {
    value: redisConnectionString
  }
}

resource applicationInsightsSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'storefront--observability--applicationinsightsconnectionstring'
  properties: {
    value: applicationInsightsConnectionString
  }
}

resource jwtSigningSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'storefront--security--signingkey'
  properties: {
    value: jwtSigningKey
  }
}
