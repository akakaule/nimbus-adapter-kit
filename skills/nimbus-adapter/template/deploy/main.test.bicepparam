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
//#if (InboxSql)
param keyVaultName = '<key-vault-test>'
param integrationDatabaseSecretName = 'integration-db-connection-string'
//#endif
//#if (InboxTable)
param inboxStorageAccountName = '<stinboxtest>'
//#endif
