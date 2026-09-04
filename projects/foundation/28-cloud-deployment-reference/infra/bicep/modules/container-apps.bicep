param namePrefix string
param location string
param tags object
param logAnalyticsCustomerId string
@secure()
param logAnalyticsSharedKey string
param infrastructureSubnetId string
param registryServer string
param identityId string
param keyVaultUri string
param imageTag string
param minReplicas int
param maxReplicas int
param environmentName string
param postgresHost string
param redisHost string
param serviceBusNamespace string

resource managedEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${namePrefix}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsCustomerId
        sharedKey: logAnalyticsSharedKey
      }
    }
    vnetConfiguration: empty(infrastructureSubnetId) ? null : {
      infrastructureSubnetId: infrastructureSubnetId
      internal: false
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
        minimumCount: 0
        maximumCount: 0
      }
    ]
  }
}

var keyVaultBase = endsWith(keyVaultUri, '/') ? keyVaultUri : '${keyVaultUri}/'
var sharedSecrets = [
  {
    name: 'database-connection'
    keyVaultUrl: '${keyVaultBase}secrets/storefront--database--connectionstring'
    identity: identityId
  }
  {
    name: 'redis-connection'
    keyVaultUrl: '${keyVaultBase}secrets/storefront--cache--connectionstring'
    identity: identityId
  }
  {
    name: 'application-insights'
    keyVaultUrl: '${keyVaultBase}secrets/storefront--observability--applicationinsightsconnectionstring'
    identity: identityId
  }
  {
    name: 'jwt-signing-key'
    keyVaultUrl: '${keyVaultBase}secrets/storefront--security--signingkey'
    identity: identityId
  }
]

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-${namePrefix}-api'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
    }
  }
  properties: {
    managedEnvironmentId: managedEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Multiple'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      }
      registries: [
        {
          server: registryServer
          identity: identityId
        }
      ]
      secrets: sharedSecrets
    }
    template: {
      revisionSuffix: 'initial'
      containers: [
        {
          name: 'api'
          image: '${registryServer}/storefront-api:${imageTag}'
          env: [
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'ASPNETCORE_URLS'
              value: 'http://+:8080'
            }
            {
              name: 'Database__Provider'
              value: 'Postgres'
            }
            {
              name: 'Database__ConnectionString'
              secretRef: 'database-connection'
            }
            {
              name: 'Cache__Provider'
              value: 'Redis'
            }
            {
              name: 'Cache__ConnectionString'
              secretRef: 'redis-connection'
            }
            {
              name: 'Messaging__Provider'
              value: 'ServiceBus'
            }
            {
              name: 'Messaging__FullyQualifiedNamespace'
              value: serviceBusNamespace
            }
            {
              name: 'Messaging__QueueName'
              value: 'storefront-orders'
            }
            {
              name: 'KeyVault__Enabled'
              value: 'true'
            }
            {
              name: 'KeyVault__VaultUri'
              value: keyVaultUri
            }
            {
              name: 'Security__SigningKey'
              secretRef: 'jwt-signing-key'
            }
            {
              name: 'Security__Authority'
              value: '${environment().authentication.loginEndpoint}${subscription().tenantId}/v2.0'
            }
            {
              name: 'Observability__Exporter'
              value: 'AzureMonitor'
            }
            {
              name: 'Observability__ApplicationInsightsConnectionString'
              secretRef: 'application-insights'
            }
            {
              name: 'Deployment__Environment'
              value: environmentName
            }
            {
              name: 'Dependencies__PostgresHost'
              value: postgresHost
            }
            {
              name: 'Dependencies__RedisHost'
              value: redisHost
            }
          ]
          probes: [
            {
              type: 'Startup'
              httpGet: {
                path: '/health/startup'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 1
              periodSeconds: 2
              failureThreshold: 30
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: 8080
                scheme: 'HTTP'
              }
              periodSeconds: 10
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
                scheme: 'HTTP'
              }
              periodSeconds: 5
              failureThreshold: 3
            }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'http'
            http: {
              metadata: {
                concurrentRequests: '50'
              }
            }
          }
        ]
      }
    }
  }
}

resource migrationJob 'Microsoft.App/jobs@2024-03-01' = {
  name: 'caj-${namePrefix}-migrations'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
    }
  }
  properties: {
    environmentId: managedEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 1800
      replicaRetryLimit: 1
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [
        {
          server: registryServer
          identity: identityId
        }
      ]
      secrets: sharedSecrets
    }
    template: {
      containers: [
        {
          name: 'migration-runner'
          image: '${registryServer}/storefront-migrations:${imageTag}'
          env: [
            {
              name: 'Database__Provider'
              value: 'Postgres'
            }
            {
              name: 'Database__ConnectionString'
              secretRef: 'database-connection'
            }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
    }
  }
}

output apiFqdn string = api.properties.configuration.ingress.fqdn
output migrationJobName string = migrationJob.name
