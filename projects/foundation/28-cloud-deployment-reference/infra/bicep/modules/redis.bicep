param namePrefix string
param location string
param tags object
param skuName string
param family string
param capacity int
param publicNetworkAccess string

var redisName = take(toLower('redis-${namePrefix}'), 63)

resource redis 'Microsoft.Cache/redis@2023-08-01' = {
  name: redisName
  location: location
  tags: tags
  properties: {
    enableNonSslPort: false
    minimumTlsVersion: '1.2'
    publicNetworkAccess: publicNetworkAccess
    redisConfiguration: {
      'maxmemory-policy': 'volatile-lru'
    }
    sku: {
      name: skuName
      family: family
      capacity: capacity
    }
  }
}

output id string = redis.id
output host string = redis.properties.hostName
@secure()
output connectionString string = '${redis.properties.hostName}:6380,password=${redis.listKeys().primaryKey},ssl=True,abortConnect=False'
