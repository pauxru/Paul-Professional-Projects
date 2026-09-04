param enabled bool
param namePrefix string
param location string
param tags object
param subnetId string
param keyVaultId string
param registryId string
param redisId string
param serviceBusId string

resource keyVaultEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = if (enabled) {
  name: 'pe-kv-${namePrefix}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: subnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'key-vault'
        properties: {
          privateLinkServiceId: keyVaultId
          groupIds: [
            'vault'
          ]
        }
      }
    ]
  }
}

resource registryEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = if (enabled) {
  name: 'pe-acr-${namePrefix}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: subnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'registry'
        properties: {
          privateLinkServiceId: registryId
          groupIds: [
            'registry'
          ]
        }
      }
    ]
  }
}

resource redisEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = if (enabled) {
  name: 'pe-redis-${namePrefix}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: subnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'redis'
        properties: {
          privateLinkServiceId: redisId
          groupIds: [
            'redisCache'
          ]
        }
      }
    ]
  }
}

resource serviceBusEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = if (enabled) {
  name: 'pe-sb-${namePrefix}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: subnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'service-bus'
        properties: {
          privateLinkServiceId: serviceBusId
          groupIds: [
            'namespace'
          ]
        }
      }
    ]
  }
}
