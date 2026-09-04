param namePrefix string
param location string
param tags object
param retentionDays int

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${namePrefix}'
  location: location
  tags: tags
  properties: {
    retentionInDays: retentionDays
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${namePrefix}'
  location: location
  kind: 'web'
  tags: tags
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    DisableLocalAuth: true
  }
}

output workspaceId string = workspace.id
output customerId string = workspace.properties.customerId
@secure()
output sharedKey string = workspace.listKeys().primarySharedKey
@secure()
output applicationInsightsConnectionString string = applicationInsights.properties.ConnectionString
