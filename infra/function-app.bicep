@minLength(3)
@maxLength(4)
@description('Name of the environment.')
param environmentName string

@description('Short name used as a prefix for Azure resources. Keep it globally unique where required.')
param appShortName string

@description('Name of the shared user-assigned managed identity used for identitying the function app.')
param userAssignedManagedIdentityName string

@description('App settings to project into the container app environment.')
param appSettings array = []

@description('Custom domain name bind to the container app.')
param customDomainName string = ''

@description('Memory allocated to each function instance in MB.')
param instanceMemoryMB int = 512

@description('Maximum number of function instances that can be running simultaneously.')
param maximumInstanceCount int = 10

@description('Id of the user identity to be used for testing and debugging. This is not required in production. Leave empty if not needed. Can optionally use deployer().objectId if manually deployed')
param userIdentityPrincipalId string = ''

@description('Indicates whether the deployment is interactive.')
param isInteractiveDeployer bool = true

var location = resourceGroup().location
var abbrs = loadJsonContent('./abbreviations.json')
var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))

var functionAppName = '${appShortName}-${abbrs.webSitesFunctions}${environmentName}'
var appServicePlanName = '${appShortName}-${abbrs.webServerFarms}${environmentName}'
var storageAccountName = replace('${appShortName}-${abbrs.storageStorageAccounts}s${environmentName}', '-', '')
var logAnalyticsName = '${appShortName}-${abbrs.operationalInsightsWorkspaces}${environmentName}'
var appInsightsName = '${appShortName}-${abbrs.insightsComponents}${environmentName}'
var keyVaultName = '${appShortName}-${abbrs.keyVaultVaults}${environmentName}'
var deploymentStorageContainerName = 'app-package-${take(functionAppName, 32)}-${take(toLower(uniqueString(functionAppName, resourceToken)), 7)}'

resource sharedManagedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: userAssignedManagedIdentityName
  location: location
}

var allAppSettings = union(
    reduce(appSettings, {}, (currentObj, nextItem) => union(currentObj, {
      '${startsWith(nextItem.name, 'KV_') ? substring(nextItem.name, 3) : nextItem.name}': startsWith(nextItem.name, 'KV_')
        ? '@Microsoft.KeyVault(SecretUri=https://${keyVaultName}.vault.azure.net/secrets/${replace(substring(nextItem.name, 3), '_', '-')}/)'
        : nextItem.value
    })),
    {
      AZURE_FUNCTIONS_ENVIRONMENT: environmentName
      APPLICATIONINSIGHTS_AUTHENTICATION_STRING: 'ClientId=${sharedManagedIdentity.properties.clientId};Authorization=AAD'
      AzureFunctionsJobHost__logging__logLevel__default: 'Information'
    }
)

module logAnalytics 'br/public:avm/res/operational-insights/workspace:0.15.1' = {
  params: {
    name: logAnalyticsName
    location: location
  }
}

module appInsights 'br/public:avm/res/insights/component:0.7.2' = {
  params: {
    name: appInsightsName
    workspaceResourceId: logAnalytics.outputs.resourceId
    location: location
  }
}

module storageAccount 'br/public:avm/res/storage/storage-account:0.32.0' = {
  params: {
    name: storageAccountName
    location: location
    skuName: 'Standard_LRS'
    kind: 'StorageV2'
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
    }
    blobServices: {
      containers: [{name: deploymentStorageContainerName}]
    }
  }
}

// Create an App Service Plan to group applications under the same payment plan and SKU
module appServicePlan 'br/public:avm/res/web/serverfarm:0.7.0' = {
  params: {
    name: appServicePlanName
    skuName: 'FC1'
    reserved: true
    location: location
  }
}

// Create a Flex Consumption Function App to host the API
module functionApp 'br/public:avm/res/web/site:0.24.0' = {
  params: {
    kind: 'functionapp,linux'
    name: functionAppName
    location: location
    tags: { 'azd-service-name': appShortName }
    serverFarmResourceId: appServicePlan.outputs.resourceId
    keyVaultAccessIdentityResourceId: sharedManagedIdentity.id
    managedIdentities: {
      systemAssigned: true
      userAssignedResourceIds: [sharedManagedIdentity.id]
    }
    publicNetworkAccess: 'Enabled'
    siteConfig: {
      alwaysOn: false
      cors: {
        allowedOrigins: [
          '*'
        ]
      }
    }
    functionAppConfig: {
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'       
      }
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storageAccount.outputs.serviceEndpoints.blob}${deploymentStorageContainerName}'
          authentication: {
            userAssignedIdentityResourceId: sharedManagedIdentity.id
            type: 'UserAssignedIdentity'
          }
        }
      }
      scaleAndConcurrency: {
        instanceMemoryMB: instanceMemoryMB
        maximumInstanceCount: maximumInstanceCount
      }
    }
    configs: [{
        name: 'appsettings'
        properties: allAppSettings
        applicationInsightResourceId: appInsights.outputs.resourceId
    }]
    hostNameBindings: !empty(customDomainName) ? [{
        name: customDomainName
    }] : []
  }
}

// Managed Certificate (Native resource type)
// Note: This must execute AFTER the Function App is built and hostname is bound.
resource managedCertificate 'Microsoft.Web/sites/certificates@2025-03-01' = if (!empty(customDomainName)) {
  name: '${functionAppName}/${customDomainName}-${functionAppName}'
  location: location
  properties: {
    serverFarmId: appServicePlan.outputs.resourceId
    canonicalName: customDomainName
  }
  dependsOn: [
    functionApp
  ]
}

// Enable SNI Binding on the hostname (Native sub-resource extension)
resource hostNameBindingSsl 'Microsoft.Web/sites/hostNameBindings@2023-12-01' = if (!empty(customDomainName)) {
  name: '${functionAppName}/${customDomainName}'
  properties: {
    sslState: 'SniEnabled'
    thumbprint: managedCertificate.properties.thumbprint
  }
  dependsOn: [
    functionApp
  ]
}

module keyVault 'br/public:avm/res/key-vault/vault:0.14.0' = {
  params: {
    name: keyVaultName
    enablePurgeProtection: false
  }
}

module rbac 'rbac.bicep' = {
  params: {
    userIdentityPrincipalId: userIdentityPrincipalId
    managedIdentityPrincipalId: sharedManagedIdentity.properties.principalId
    appInsightsName: appInsightsName
    keyVaultName: keyVaultName
    storageAccountNames: [storageAccountName]
  }
}

// Role assignment for Storage Account (Blob) - Deployer Identity
var deployerPrincipalId = deployer().objectId
var storageRoleDefinitionId = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b' //Storage Blob Data Owner role
var keyVaultSecretRoleDefinitionId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7' // Key Vault Secret Reader role ID
resource storageAccountResource 'Microsoft.Storage/storageAccounts@2022-09-01' existing = {
  name: storageAccountName
  dependsOn: [
    storageAccount
  ]
}
resource storageRoleAssignment_Deployer 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccountResource.id, deployerPrincipalId, storageRoleDefinitionId)
  scope: storageAccountResource
  properties: {
    roleDefinitionId: resourceId('Microsoft.Authorization/roleDefinitions', storageRoleDefinitionId)
    principalId: deployerPrincipalId // Use deployer identity ID
    principalType: isInteractiveDeployer ? 'User' : 'ServicePrincipal'
  }
}
resource keyVaultResource 'Microsoft.KeyVault/vaults@2026-02-01' existing = {
  name: keyVaultName
  dependsOn: [
    keyVault
  ]
}
resource keyVaultSecretRoleAssignment_Deployer 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVaultResource.id, deployerPrincipalId, keyVaultSecretRoleDefinitionId)
  scope: keyVaultResource
  properties: {
    roleDefinitionId: resourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretRoleDefinitionId)
    principalId: deployerPrincipalId // Use deployer identity ID
    principalType: isInteractiveDeployer ? 'User' : 'ServicePrincipal'
  }
}

output functionAppName string = functionApp.outputs.name
output storageAccountName string = storageAccount.outputs.name
output appInsightsName string = appInsights.outputs.name
output keyVaultName string = keyVault.outputs.name