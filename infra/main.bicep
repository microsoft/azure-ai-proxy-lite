targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name which is used to generate a short unique hash for each resource')
param name string

@minLength(1)
@description('Primary location for all resources')
@metadata({
  azd: {
    type: 'location'
    title: 'Primary resource location'
  }
})
param location string

param proxyAppExists bool = false

param adminAppExists bool = false

@description('Entra ID (Azure AD) Client ID for admin UI authentication. Leave empty to use username/password auth.')
param entraClientId string = ''

@description('Entra ID (Azure AD) Tenant ID for admin UI authentication. Leave empty to use username/password auth.')
param entraTenantId string = ''

@description('Location for the Registration app resource group')
@allowed(['centralus', 'eastus2', 'eastasia', 'westeurope', 'westus2'])
@metadata({
  azd: {
    type: 'location'
    title: 'Static Web App (registration) location'
  }
})
param swaLocation string

@description('Location for the Azure AI Foundry project and model deployments')
@metadata({
  azd: {
    type: 'location'
    title: 'AI Foundry project location'
  }
})
param foundryLocation string

@secure()
@description('High-entropy encryption key used to protect stored secrets and internal cache invalidation.')
param encryptionKey string

var resourceToken = toLower(uniqueString(subscription().id, name, location))
var tags = { 'azd-env-name': name }
var prefix = '${name}-${resourceToken}'

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: '${name}-rg'
  location: location
  tags: tags
}

// Container apps host (including container registry)
module containerApps 'core/host/container-apps.bicep' = {
  name: 'container-apps'
  scope: resourceGroup
  params: {
    location: location
    tags: tags
    containerAppsEnvironmentName: '${prefix}-cae'
    containerRegistryName: '${replace(prefix, '-', '')}registry'
    logAnalyticsWorkspaceName: monitoring.outputs.logAnalyticsWorkspaceName
  }
}

// Proxy app (API proxy only)
module proxy 'app/proxy.bicep' = {
  name: 'proxy'
  scope: resourceGroup
  params: {
    name: 'proxy-${resourceToken}'
    location: location
    tags: tags
    identityName: '${prefix}-id-proxy'
    containerAppsEnvironmentName: containerApps.outputs.environmentName
    containerRegistryName: containerApps.outputs.registryName
    exists: proxyAppExists
    storageAccountName: storageAccount.outputs.name
    encryptionKey: encryptionKey
    registrationUrl: registration.outputs.SERVICE_WEB_URI
    appInsightsConnectionString: monitoring.outputs.applicationInsightsConnectionString
  }
}

// Admin app (management UI)
module admin 'app/admin.bicep' = {
  name: 'admin'
  scope: resourceGroup
  params: {
    name: 'admin-${resourceToken}'
    location: location
    tags: tags
    identityName: '${prefix}-id-admin'
    containerAppsEnvironmentName: containerApps.outputs.environmentName
    containerRegistryName: containerApps.outputs.registryName
    exists: adminAppExists
    storageAccountName: storageAccount.outputs.name
    encryptionKey: encryptionKey
    registrationUrl: registration.outputs.SERVICE_WEB_URI
    proxyInternalUrl: 'https://${proxy.outputs.SERVICE_PROXY_NAME}.internal.${containerApps.outputs.defaultDomain}'
    entraClientId: entraClientId
    entraTenantId: entraTenantId
    appInsightsConnectionString: monitoring.outputs.applicationInsightsConnectionString
  }
}

// Azure Storage Account (Table Storage for all data)
module storageAccount 'storage.bicep' = {
  name: 'storage-account'
  scope: resourceGroup
  params: {
    name: take('${replace(prefix, '-', '')}st', 24)
    location: location
    tags: tags
  }
}

// Grant proxy identity Storage Table Data Contributor scoped to the storage account.
module storageRoleProxy 'core/security/storage-role-assignment.bicep' = {
  name: 'storage-role-proxy'
  scope: resourceGroup
  params: {
    storageAccountName: storageAccount.outputs.name
    principalId: proxy.outputs.SERVICE_PROXY_IDENTITY_PRINCIPAL_ID
    roleDefinitionId: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3' // Storage Table Data Contributor
    principalType: 'ServicePrincipal'
  }
}

// Grant admin identity Storage Table Data Contributor scoped to the storage account.
module storageRoleAdmin 'core/security/storage-role-assignment.bicep' = {
  name: 'storage-role-admin'
  scope: resourceGroup
  params: {
    storageAccountName: storageAccount.outputs.name
    principalId: admin.outputs.SERVICE_ADMIN_IDENTITY_PRINCIPAL_ID
    roleDefinitionId: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3' // Storage Table Data Contributor
    principalType: 'ServicePrincipal'
  }
}

// The Registration frontend
module registration 'registration.bicep' = {
  name: 'registration'
  scope: resourceGroup
  params: {
    name: '${prefix}-registration'
    location: swaLocation
    tags: tags
  }
}

// link Registration to Proxy backend
module swaLinkDotnet './linkSwaResource.bicep' = {
  name: 'frontend-link-dotnet'
  scope: resourceGroup
  params: {
    swaAppName: registration.outputs.SERVICE_WEB_NAME
    backendAppName: proxy.outputs.SERVICE_PROXY_NAME
  }
}

// Azure AI Foundry project (empty — deploy models into it)
module foundry 'foundry.bicep' = {
  name: 'foundry'
  scope: resourceGroup
  params: {
    name: prefix
    location: foundryLocation
    tags: tags
    proxyPrincipalId: proxy.outputs.SERVICE_PROXY_IDENTITY_PRINCIPAL_ID
  }
}

// Monitor application with Azure Monitor
module monitoring 'core/monitor/monitoring.bicep' = {
  name: 'monitoring'
  scope: resourceGroup
  params: {
    location: location
    tags: tags
    applicationInsightsDashboardName: '${prefix}-appinsights-dashboard'
    applicationInsightsName: '${prefix}-appinsights'
    logAnalyticsName: '${take(prefix, 50)}-loganalytics' // Max 63 chars
  }
}

output AZURE_LOCATION string = location
output AZURE_CONTAINER_ENVIRONMENT_NAME string = containerApps.outputs.environmentName
output AZURE_CONTAINER_REGISTRY_NAME string = containerApps.outputs.registryName
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = containerApps.outputs.registryLoginServer

output SERVICE_PROXY_IDENTITY_PRINCIPAL_ID string = proxy.outputs.SERVICE_PROXY_IDENTITY_PRINCIPAL_ID
output SERVICE_PROXY_NAME string = proxy.outputs.SERVICE_PROXY_NAME
output SERVICE_PROXY_URI string = proxy.outputs.SERVICE_PROXY_URI
output SERVICE_PROXY_IMAGE_NAME string = proxy.outputs.SERVICE_PROXY_IMAGE_NAME

output SERVICE_ADMIN_NAME string = admin.outputs.SERVICE_ADMIN_NAME
output SERVICE_ADMIN_URI string = admin.outputs.SERVICE_ADMIN_URI
output SERVICE_ADMIN_IMAGE_NAME string = admin.outputs.SERVICE_ADMIN_IMAGE_NAME

output SERVICE_REGISTRATION_URI string = registration.outputs.SERVICE_WEB_URI

output SERVICE_STORAGE_ACCOUNT_NAME string = storageAccount.outputs.name

output SERVICE_AI_SERVICES_NAME string = foundry.outputs.aiServicesName
output SERVICE_AI_SERVICES_ENDPOINT string = foundry.outputs.aiServicesEndpoint
output SERVICE_AI_PROJECT_NAME string = foundry.outputs.aiProjectName
