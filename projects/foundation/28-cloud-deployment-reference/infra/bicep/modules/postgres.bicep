param namePrefix string
param location string
param tags object
param administratorLogin string
@secure()
param administratorPassword string
param skuName string
param storageGb int
param zoneRedundant bool
param delegatedSubnetId string
param privateDnsZoneId string
param publicNetworkAccess string

var serverName = take(toLower('psql-${namePrefix}'), 63)

resource server 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: serverName
  location: location
  tags: tags
  sku: {
    name: skuName
    tier: startsWith(skuName, 'B_') ? 'Burstable' : 'GeneralPurpose'
  }
  properties: {
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorPassword
    version: '16'
    availabilityZone: '1'
    backup: {
      backupRetentionDays: zoneRedundant ? 35 : 7
      geoRedundantBackup: zoneRedundant ? 'Enabled' : 'Disabled'
    }
    highAvailability: {
      mode: zoneRedundant ? 'ZoneRedundant' : 'Disabled'
      standbyAvailabilityZone: zoneRedundant ? '2' : null
    }
    storage: {
      storageSizeGB: storageGb
      autoGrow: 'Enabled'
    }
    network: {
      delegatedSubnetResourceId: empty(delegatedSubnetId) ? null : delegatedSubnetId
      privateDnsZoneArmResourceId: empty(privateDnsZoneId) ? null : privateDnsZoneId
      publicNetworkAccess: publicNetworkAccess
    }
    authConfig: {
      activeDirectoryAuth: 'Enabled'
      passwordAuth: 'Enabled'
      tenantId: subscription().tenantId
    }
  }
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: server
  name: 'storefront'
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

output id string = server.id
output host string = server.properties.fullyQualifiedDomainName
