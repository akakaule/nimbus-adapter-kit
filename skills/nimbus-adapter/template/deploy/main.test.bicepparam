using './main.bicep'

// Parameters for the test environment.
// Replace every <placeholder>; the pipeline passes imageTag.

param environmentName = 'test'
param serviceBusNamespaceName = '<sb-namespace-test>'
param containerAppsEnvironmentName = '<container-apps-environment-test>'
param containerRegistryName = '<containerregistry>'
param imageTag = readEnvironmentVariable('IMAGE_TAG', 'latest')
param externalSystemBaseUrl = '<https://externalsystem-test.example>'
param externalSystemScope = ''
param applicationInsightsConnectionString = ''
//#if (Inbox)
param integrationDatabaseConnectionString = az.getSecret('<subscription-id>', '<key-vault-resource-group>', '<key-vault-name>', 'integration-db-connection-string')
//#endif
