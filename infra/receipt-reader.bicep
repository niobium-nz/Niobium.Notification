param serviceBusNamespaceName string
param queueName string
param principalId string

// Azure Service Bus Data Receiver. Restrict the role assignment to one queue.
var receiverRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')

resource serviceBus 'Microsoft.ServiceBus/namespaces@2026-01-01' existing = {
  name: serviceBusNamespaceName
}

resource destination 'Microsoft.ServiceBus/namespaces/queues@2026-01-01' existing = {
  name: queueName
  parent: serviceBus
}

resource reader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(destination.id, principalId, receiverRole)
  scope: destination
  properties: {
    roleDefinitionId: receiverRole
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}
