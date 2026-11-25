using './main.bicep'

// Parameters for the prod environment.
// Replace every <placeholder>; the pipeline passes imageTag.

param environmentName = 'prod'
param serviceBusNamespaceName = '<sb-namespace-prod>'
param containerAppsEnvironmentName = '<container-apps-environment-prod>'
param containerRegistryName = '<containerregistry>'
param imageTag = readEnvironmentVariable('IMAGE_TAG', 'latest')
param externalSystemBaseUrl = '<https://externalsystem-prod.example>'
param externalSystemScope = ''
param applicationInsightsConnectionString = ''
//#if (InboxSql)
param keyVaultName = '<key-vault-prod>'
param integrationDatabaseSecretName = 'integration-db-connection-string'
//#endif
//#if (InboxTable)
param inboxStorageAccountName = '<stinboxprod>'
//#endif
