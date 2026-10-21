// The adapter as an Azure Container App in an existing NimBus environment.
//
// Creates: a user-assigned managed identity, its Service Bus and ACR role assignments, and the
// Container App with a KEDA Service Bus scale rule. Expects (existing): the NimBus Service Bus
// namespace, a Container Apps environment and a container registry. The Service Bus topology is
// NOT created here: the pipeline applies it from the catalog with `nb topology apply`.
//
// Validate locally:  az bicep build --file deploy/main.bicep
// Preview a change:  az deployment group what-if -g <rg> -f deploy/main.bicep -p deploy/main.dev.bicepparam

targetScope = 'resourceGroup'

@description('Short adapter name, lower case, used in resource names (Container App names allow 32 characters).')
@maxLength(20)
param adapterName string = 'externalsystem'

@description('Environment name, e.g. dev, test, prod.')
@allowed([ 'dev', 'test', 'prod' ])
param environmentName string

param location string = resourceGroup().location

@description('The NimBus Service Bus namespace (existing, same resource group).')
param serviceBusNamespaceName string

@description('The Container Apps environment to run in (existing, same resource group).')
param containerAppsEnvironmentName string

@description('The container registry holding the adapter image (existing, same resource group).')
param containerRegistryName string

@description('Image tag to deploy, e.g. 1.4.0 or the pipeline build number.')
param imageTag string

@description('Base URL of the ExternalSystem API.')
param externalSystemBaseUrl string

@description('Entra ID scope for the ExternalSystem API, e.g. https://{org}.crm.dynamics.com/.default.')
param externalSystemScope string = ''

@description('Application Insights connection string (not a secret, but kept out of the template).')
param applicationInsightsConnectionString string = ''
//#if (Inbox)

@description('Key Vault (existing, same resource group, Azure RBAC permission model) that holds the inbox connection string.')
param keyVaultName string

@description('Name of the Key Vault secret holding the integration database connection string (the inbox).')
param integrationDatabaseSecretName string = 'integration-db-connection-string'
//#endif

//#if (IngressEnabled)
@description('Ingress queue of this adapter for ExternalSystem change notifications (created here, in the NimBus namespace).')
param ingressQueueName string = 'externalsystem-changes'

//#endif
@description('Sessions processed in parallel per replica. Size it to what ExternalSystem tolerates.')
param maxConcurrentSessions int = 8

@minValue(1)
param minReplicas int = 1

@minValue(1)
param maxReplicas int = 3

// The endpoint (topic and subscription) this adapter receives on. Must equal the catalog endpoint.
var endpointName = 'ExternalSystemEndpoint'
var resourceSuffix = '${adapterName}-${environmentName}'

// Built-in role definition ids.
var serviceBusDataReceiver = '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
var serviceBusDataSender = '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
var acrPull = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
//#if (Inbox)
var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'
//#endif

resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = {
  name: serviceBusNamespaceName
}

//#if (IngressEnabled)
// Settings match the provisioner's (local runs): poison notifications dead-letter after 10 attempts.
resource ingressQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: serviceBus
  name: ingressQueueName
  properties: {
    maxDeliveryCount: 10
    lockDuration: 'PT1M'
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P7D'
  }
}

//#endif
resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2025-07-01' existing = {
  name: containerAppsEnvironmentName
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: containerRegistryName
}

//#if (Inbox)
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

//#endif
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${resourceSuffix}'
  location: location
}

// Receive and settle messages; send retries, deferrals and responses to the Resolver.
resource receiverRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBus.id, identity.id, serviceBusDataReceiver)
  scope: serviceBus
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataReceiver)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource senderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBus.id, identity.id, serviceBusDataSender)
  scope: serviceBus
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataSender)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource pullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, identity.id, acrPull)
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPull)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

//#if (Inbox)
// The app reads the inbox connection string from Key Vault at runtime with its own identity, so
// the secret never passes through the deployment (no vault deployment permission needed).
resource secretsUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, identity.id, keyVaultSecretsUser)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

//#endif
resource adapter 'Microsoft.App/containerApps@2025-07-01' = {
  name: 'ca-${resourceSuffix}'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnvironment.id
    configuration: {
      // A worker: no ingress. Health is visible in the logs and in the NimBus WebApp.
      activeRevisionsMode: 'Single'
      registries: [
        {
          server: registry.properties.loginServer
          identity: identity.id
        }
      ]
//#if (Inbox)
      secrets: [
        {
          name: 'integration-db'
          keyVaultUrl: '${keyVault.properties.vaultUri}secrets/${integrationDatabaseSecretName}'
          identity: identity.id
        }
      ]
//#endif
    }
    template: {
      containers: [
        {
          name: 'adapter'
          image: '${registry.properties.loginServer}/${adapterName}-adapter:${imageTag}'
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            // DefaultAzureCredential picks the user-assigned identity by its client id.
            { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
            // A namespace host name (no key): the Aspire client then authenticates with the identity.
            { name: 'ConnectionStrings__servicebus', value: '${serviceBus.name}.servicebus.windows.net' }
//#if (Inbox)
            { name: 'ConnectionStrings__integration', secretRef: 'integration-db' }
//#endif
            { name: 'ExternalSystem__BaseUrl', value: externalSystemBaseUrl }
            { name: 'ExternalSystem__Scope', value: externalSystemScope }
            { name: 'Adapter__MaxConcurrentSessions', value: string(maxConcurrentSessions) }
//#if (IngressEnabled)
            { name: 'Ingress__QueueName', value: ingressQueue.name }
//#endif
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: applicationInsightsConnectionString }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [
          {
            // Scales on the backlog of the adapter's subscription. Authenticates with the identity.
            name: 'servicebus-backlog'
            custom: {
              type: 'azure-servicebus'
              identity: identity.id
              metadata: {
                namespace: serviceBus.name
                topicName: endpointName
                subscriptionName: endpointName
                messageCount: '50'
              }
            }
          }
//#if (IngressEnabled)
          {
            // And on the backlog of change notifications waiting to be published.
            name: 'ingress-backlog'
            custom: {
              type: 'azure-servicebus'
              identity: identity.id
              metadata: {
                namespace: serviceBus.name
                queueName: ingressQueue.name
                messageCount: '50'
              }
            }
          }
//#endif
        ]
      }
    }
  }
  dependsOn: [
    receiverRole
    senderRole
    pullRole
//#if (Inbox)
    secretsUserRole
//#endif
  ]
}

@description('Principal id of the adapter identity: grant it access to ExternalSystem (application user / API permissions).')
output identityPrincipalId string = identity.properties.principalId

@description('Client id of the adapter identity.')
output identityClientId string = identity.properties.clientId

output containerAppName string = adapter.name
