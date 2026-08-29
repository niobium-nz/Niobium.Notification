@description('Name of the Service Bus namespace')
param serviceBusNamespaceName string

@description('Name of the Queues')
param serviceBusQueueNames array = []

// Only create Service Bus resources when queue names are provided
var createServiceBus = !empty(serviceBusQueueNames)

@description('Location for all resources.')
var location = resourceGroup().location

@description('Specifies the SKU to use for the Service Bus namespace.')
@allowed([
  'Basic'
  'Standard'
  'Premium'
])
param serviceBusSku string = 'Basic'

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2026-01-01' = if (createServiceBus) {
  name: serviceBusNamespaceName
  location: location
  sku: {
    name: serviceBusSku
    tier: serviceBusSku
  }
  properties: {}
}

resource serviceBusQueues 'Microsoft.ServiceBus/namespaces/queues@2026-01-01' = [for serviceBusQueueName in serviceBusQueueNames: if (createServiceBus) {
  parent: serviceBusNamespace
  name: serviceBusQueueName
  properties: {
    lockDuration: 'PT5M'
    maxSizeInMegabytes: 1024
    requiresDuplicateDetection: false
    requiresSession: false
    defaultMessageTimeToLive: 'P14D'
    deadLetteringOnMessageExpiration: true
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    maxDeliveryCount: 10
    enablePartitioning: false
    enableExpress: false
  }
}]

resource sendAuthorizationRules 'Microsoft.ServiceBus/namespaces/queues/authorizationRules@2026-01-01' = [for (serviceBusQueueName, i) in serviceBusQueueNames: if (createServiceBus) {
  name: '${serviceBusQueueName}-2'
  parent: serviceBusQueues[i]
  properties: {
    rights: [
      'Send'
    ]
  }
}]

resource listenAuthorizationRules 'Microsoft.ServiceBus/namespaces/queues/authorizationRules@2026-01-01' = [for (serviceBusQueueName, i) in serviceBusQueueNames: if (createServiceBus) {
  name: '${serviceBusQueueName}-8'
  parent: serviceBusQueues[i]
  properties: {
    rights: [
      'Listen'
    ]
  }
}]

output name string = createServiceBus ? serviceBusNamespaceName : ''
output fullyQualifiedNamespace string = createServiceBus ? first(split(replace(replace(serviceBusNamespace.properties.serviceBusEndpoint, 'https://', ''), '/', ''), ':')) : ''
