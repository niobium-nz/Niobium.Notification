@description('Name of the Service Bus namespace')
param serviceBusNamespaceName string

@description('Name of the Container App Managed Environment')
param containerAppsEnvironmentName string

@description('Name of the Pub/Sub Dapr App Id')
param pubSubDaprAppId string

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2026-01-01' existing = {
  name: serviceBusNamespaceName
}

resource managedEnvironment 'Microsoft.App/managedEnvironments@2025-07-01' existing = {
  name: containerAppsEnvironmentName
}

resource serviceBusPubSub 'Microsoft.App/managedEnvironments/daprComponents@2025-07-01' = {
  parent: managedEnvironment
  name: 'servicebus-pubsub'

  properties: {
    componentType: 'pubsub.azure.servicebus.queues'
    version: 'v1'
    ignoreErrors: false
    initTimeout: '30s'

    metadata: [
      {
        name: 'namespaceName'
        value: '${serviceBusNamespace.name}.servicebus.windows.net'
      }
      {
        // Queue is provisioned separately by Bicep.
        name: 'disableEntityManagement'
        value: 'true'
      }
      {
        name: 'maxConcurrentHandlers'
        value: '8'
      }
      {
        name: 'maxActiveMessages'
        value: '16'
      }
      {
        name: 'handlerTimeoutInSec'
        value: '300'
      }
      {
        name: 'lockRenewalInSec'
        value: '20'
      }
      {
        name: 'rawPayload'
        value: 'true'
      }
    ]

    // This is the Dapr app ID, not the Container App name.
    scopes: [
      pubSubDaprAppId
    ]
  }
}

output serviceBusPubSubDaprAppId string = pubSubDaprAppId