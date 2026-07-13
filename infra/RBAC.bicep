@description('Name of the Service Bus namespace')
param serviceBusNamespaceName string

@description('Name of the Storage Account')
param storageAccountName string

@description('Specifies the principal ID to the resources that owns the data of this Service Bus namespace.')
param dataOwnerPrincipalId string

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = {
  name: serviceBusNamespaceName
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2026-04-01' existing = {
  name: storageAccountName
}

@description('This is the built-in Azure Service Bus Data Owner role. See https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles#storage-account-contributor')
resource serviceBusDataOwnerRoleDefinition 'Microsoft.Authorization/roleDefinitions@2022-04-01' existing = {
  scope: subscription()
  name: '090c5cfd-751d-490a-894a-3ce6f1109419'
}

resource serviceBusDataOwnerRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, dataOwnerPrincipalId, serviceBusDataOwnerRoleDefinition.id)
  scope: serviceBusNamespace
  properties: {
    roleDefinitionId: serviceBusDataOwnerRoleDefinition.id
    principalId: dataOwnerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

@description('This is the built-in Azure Storage Table Data Contributor role. See https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles#storage-account-contributor')
resource storageTableDataContributorRoleDefinition 'Microsoft.Authorization/roleDefinitions@2022-04-01' existing = {
  scope: subscription()
  name: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
}

resource storageTableDataContributorRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, dataOwnerPrincipalId, storageTableDataContributorRoleDefinition.id)
  scope: storageAccount
  properties: {
    roleDefinitionId: storageTableDataContributorRoleDefinition.id
    principalId: dataOwnerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

@description('This is the built-in Azure Storage Blob Data Contributor role. See https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles#storage-account-contributor')
resource storageBlobDataContributorRoleDefinition 'Microsoft.Authorization/roleDefinitions@2022-04-01' existing = {
  scope: subscription()
  name: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
}

resource storageBlobDataContributorRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, dataOwnerPrincipalId, storageBlobDataContributorRoleDefinition.id)
  scope: storageAccount
  properties: {
    roleDefinitionId: storageBlobDataContributorRoleDefinition.id
    principalId: dataOwnerPrincipalId
    principalType: 'ServicePrincipal'
  }
}