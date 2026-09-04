param namePrefix string
param location string
param tags object
param skuName string
param publicNetworkAccess string

var namespaceName = take(toLower('sb-${namePrefix}'), 50)

resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: namespaceName
  location: location
  tags: tags
  sku: {
    name: skuName
    tier: skuName
  }
  properties: {
    minimumTlsVersion: '1.2'
    publicNetworkAccess: publicNetworkAccess
    disableLocalAuth: true
    zoneRedundant: skuName == 'Premium'
  }
}

resource ordersQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: serviceBus
  name: 'storefront-orders'
  properties: {
    lockDuration: 'PT1M'
    maxDeliveryCount: 10
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P14D'
    enablePartitioning: skuName != 'Premium'
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT10M'
  }
}

output namespaceId string = serviceBus.id
output namespaceName string = serviceBus.name
output fullyQualifiedNamespace string = '${serviceBus.name}.servicebus.windows.net'
output queueName string = ordersQueue.name
