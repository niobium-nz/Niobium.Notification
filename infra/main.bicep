targetScope = 'resourceGroup'

@description('Name of the environment.')
param environmentName string

@description('Location for deployed resources. If empty, uses the resource group location.')
param location string = resourceGroup().location

@description('Short name used as a prefix for Azure resources. Keep it globally unique where required.')
param appName string = 'niobiumnotify-${environmentName}'

@description('Name of the Container Apps managed environment.')
param containerAppsEnvironmentName string = '${appName}-cae'

@description('Name of the Container App.')
param containerAppName string = '${appName}-ca'

@description('App settings to project into the container app environment.')
param appSettings array = []

@description('Automatically set by azd. True if the container app already exists.')
param appExists bool = true

@description('Name of the Queues, seperated by comma.')
param serviceBusQueueNames string = ''

var logAnalyticsName = '${appName}-law'
var appInsightsName = '${appName}-ai'
var storageAccountName = replace('${appName}-sa', '-', '')
var serviceBusName = '${appName}-sb'
var containerAppResourceId = resourceId('Microsoft.App/containerApps', containerAppName)
var serviceBusQueueNamesArray = empty(serviceBusQueueNames) || serviceBusQueueNames == '' ? [] : split(serviceBusQueueNames, ',')

var derivedSecrets = [for setting in appSettings: {
  name: toLower(replace(string(setting.name), '_', '-'))
  value: string(setting.value)
}]

var containerEnv = [for setting in appSettings: {
  name: string(setting.name)
  secretRef: toLower(replace(string(setting.name), '_', '-'))
}]

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

module serviceBus 'ServiceBus.bicep' = {
  params: {
    serviceBusNamespaceName: serviceBusName
    serviceBusQueueNames: serviceBusQueueNamesArray
  }
}

module storageAccount 'br/public:avm/res/storage/storage-account:0.32.0' = {
  params: {
    name: storageAccountName
    publicNetworkAccess: 'Enabled'
    networkAcls: {
        defaultAction: 'Allow'
    }
  }
}
var storageTableFqdn string = replace(replace(storageAccount.outputs.serviceEndpoints.table, 'https://', ''), '/', '')
var storageBlobFqdn string = replace(replace(storageAccount.outputs.serviceEndpoints.blob, 'https://', ''), '/', '')

var containerEnv2 = concat(containerEnv, [
  { 
      name: 'APPLICATION_INSIGHTS_CONNECTION_STRING'
      value: appInsights.outputs.connectionString
  }
  { 
      name: 'SERVICEBUSOPTIONS__FULLYQUALIFIEDNAMESPACE'
      value: serviceBus.outputs.fullyQualifiedNamespace
  }
  { 
      name: 'SERVICEBUSTRIGGEROPTIONS__FULLYQUALIFIEDNAMESPACE'
      value: serviceBus.outputs.fullyQualifiedNamespace
  }
  { 
      name: 'STORAGETABLEOPTIONS__FULLYQUALIFIEDDOMAINNAME'
      value: storageTableFqdn
  }
  { 
      name: 'STORAGEBLOBOPTIONS__FULLYQUALIFIEDDOMAINNAME'
      value: storageBlobFqdn
  }
])

module managedEnvironment 'br/public:avm/res/app/managed-environment:0.13.3' = {
  params: {
    name: containerAppsEnvironmentName
    location: location
    zoneRedundant: false
    publicNetworkAccess: 'Enabled'
    appInsightsConnectionString: appInsights.outputs.connectionString
  }
}

var currentImage = appExists ? reference(containerAppResourceId, '2026-01-01').template.containers[0].image : 'mcr.microsoft.com/dotnet/samples:dotnetapp'
module containerApp 'br/public:avm/res/app/container-app:0.21.0' = {
  params: {
    name: containerAppName
    location: location
    tags: {
        'azd-service-name': 'host'
    }
    environmentResourceId: managedEnvironment.outputs.resourceId
    managedIdentities: {
      systemAssigned: true
    }
    containers: [
      {
        name: 'app'
        image: currentImage
        env: containerEnv2
        resources: {
          cpu: any('0.25')
          memory: '0.5Gi'
        }
      }
    ]
    scaleSettings: {
        minReplicas: 0
        maxReplicas: 1
    }
    secrets: derivedSecrets
    ingressAllowInsecure: false
    ingressTargetPort: 8080
  }
}

module serviceBusAuth 'ServiceBusAuth.bicep' = {
  params: {
    serviceBusNamespaceName: serviceBusName
    dataOwnerPrincipalId: containerApp.outputs.systemAssignedMIPrincipalId!
  }
}

output containerAppId string = containerApp.outputs.resourceId
output containerAppFqdn string = containerApp.outputs.fqdn