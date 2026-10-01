using './main.bicep'

// Parameters for the dev environment. Copy this file per environment (main.test.bicepparam, ...).
// Replace every <placeholder>; the pipeline passes imageTag.

param environmentName = 'dev'
param serviceBusNamespaceName = '<sb-namespace-dev>'
param containerAppsEnvironmentName = '<container-apps-environment-dev>'
param containerRegistryName = '<containerregistry>'
param imageTag = readEnvironmentVariable('IMAGE_TAG', 'latest')
param externalSystemBaseUrl = '<https://externalsystem-dev.example>'
param externalSystemScope = ''
param applicationInsightsConnectionString = ''
//#if (Inbox)
param integrationDatabaseConnectionString = az.getSecret('<subscription-id>', '<key-vault-resource-group>', '<key-vault-name>', 'integration-db-connection-string')
//#endif
