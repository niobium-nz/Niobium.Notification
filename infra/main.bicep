targetScope = 'resourceGroup'

@minLength(3)
@maxLength(4)
@description('Name of the environment.')
param environmentName string

@description('Short name used as a prefix for Azure resources. Keep it globally unique where required.')
param appShortName string

@description('App settings to project into the container app environment.')
param appSettings array = []

@description('Automatically set by azd. True if the app already exists.')
param appExists bool = false

@description('Custom domain name bind to the container app.')
param customDomainName string = ''

@description('Name of the Queues, separated by comma.')
param serviceBusQueueNames string = ''

@description('Id of the user identity to be used for testing and debugging. This is not required in production. Leave empty if not needed. Can optionally use deployer().objectId if manually deployed')
param userIdentityPrincipalId string = ''

@description('Indicates whether the deployment is interactive.')
param isInteractiveDeployer bool = true

var location = resourceGroup().location
var abbrs = loadJsonContent('./abbreviations.json')

// User assigned managed identity to be used by the function app to reach storage and other dependencies
// Assign specific roles to this identity in the RBAC module
var sharedManagedIdentityName = '${appShortName}-${abbrs.managedIdentityUserAssignedIdentities}${environmentName}'
module sharedManagedIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.6.0' = {
  params: {
    name: sharedManagedIdentityName
    location: location
  }
}

var serviceBusNamespaceName = '${appShortName}-${abbrs.serviceBusNamespaces}${environmentName}'
module serviceBus 'service-bus.bicep' = {
  params: {
    serviceBusNamespaceName: serviceBusNamespaceName
    serviceBusQueueNames: empty(serviceBusQueueNames) ? [] : split(serviceBusQueueNames, ',')
  }
}
var serviceBusSettings = [ 
    { 
        name: 'AzureWebJobsServiceBus__fullyQualifiedNamespace'
        value: serviceBus.outputs.fullyQualifiedNamespace
    }
    { 
        name: 'AzureWebJobsServiceBus__credential'
        value: 'managedidentity'
    }
    { 
        name: 'AzureWebJobsServiceBus__clientId'
        value: sharedManagedIdentity.outputs.clientId
    }
]

var dataStorageAccountName = replace('${appShortName}-${abbrs.storageStorageAccounts}d${environmentName}', '-', '')
module dataStorageAccount 'br/public:avm/res/storage/storage-account:0.32.0' = {
  params: {
    name: dataStorageAccountName
    skuName: 'Standard_LRS'
    kind: 'StorageV2'
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
    }
    blobServices: {
      corsRules: [
        {
          allowedOrigins: ['*']
          allowedMethods: ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'MERGE', 'OPTIONS']
          maxAgeInSeconds: 0
          exposedHeaders: ['*']
          allowedHeaders: ['*']
        }
      ]
    }
    tableServices: {
      corsRules: [
        {
          allowedOrigins: ['*']
          allowedMethods: ['GET', 'POST', 'PUT', 'DELETE', 'HEAD', 'MERGE', 'OPTIONS']
          maxAgeInSeconds: 0
          exposedHeaders: ['*']
          allowedHeaders: ['*']
        }
      ]
    }
  }
}
var storageSettings = [ 
    { 
        name: 'AzureWebJobsStorage__blobServiceUri'
        value: dataStorageAccount.outputs.serviceEndpoints.blob
    }
    { 
        name: 'AzureWebJobsStorage__tableServiceUri'
        value: dataStorageAccount.outputs.serviceEndpoints.table
    }
    { 
        name: 'AzureWebJobsStorage__credential'
        value: 'managedidentity'
    }
    { 
        name: 'AzureWebJobsStorage__clientId'
        value: sharedManagedIdentity.outputs.clientId
    }
]

module app 'function-app.bicep' = {
  params: {
    appShortName: appShortName
    userAssignedManagedIdentityName: sharedManagedIdentityName
    environmentName: environmentName
    appSettings: concat(appSettings, serviceBusSettings, storageSettings)
    customDomainName: customDomainName
    userIdentityPrincipalId: userIdentityPrincipalId
    isInteractiveDeployer: isInteractiveDeployer
  }
}

module rbac 'rbac.bicep' = {
  params: {
    userIdentityPrincipalId: userIdentityPrincipalId
    managedIdentityPrincipalId: sharedManagedIdentity.outputs.principalId
    storageAccountNames: [dataStorageAccountName]
    serviceBusNamespaceNames: [serviceBusNamespaceName]
  }
}

output functionAppName string = app.outputs.functionAppName
output dataStorageAccountName string = dataStorageAccount.outputs.name
output appInsightsName string = app.outputs.appInsightsName
output keyVaultName string = app.outputs.keyVaultName