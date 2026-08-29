param managedIdentityPrincipalId string = '' // Principal ID for the Managed Identity
param userIdentityPrincipalId string = '' // Principal ID for the User Identity
param serviceBusNamespaceNames array = []
param storageAccountNames array = []
param appInsightsName string = ''
param keyVaultName string = ''

// Define Role Definition IDs. See https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles#storage-account-contributor 
var storageRoleDefinitionId  = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b' //Storage Blob Data Owner role
var tableRoleDefinitionId = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3' // Storage Table Data Contributor role
var monitoringRoleDefinitionId = '3913510d-42f4-4e42-8a64-420c390055eb' // Monitoring Metrics Publisher role ID
var serviceBusRoleDefinitionId = '090c5cfd-751d-490a-894a-3ce6f1109419' // Service Bus Data Owner role ID
var keyVaultSecretRoleDefinitionId = '4633458b-17de-408a-b874-0445c86b69e6' // Key Vault Secret Reader role ID

resource storageAccounts 'Microsoft.Storage/storageAccounts@2022-09-01' existing = [for saName in storageAccountNames: {
  name: saName
}]

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

resource keyVault 'Microsoft.KeyVault/vaults@2022-07-01' existing = {
  name: keyVaultName
}

// Existing Service Bus namespaces defined by name(s)
resource serviceBusNamespaces 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = [for nsName in serviceBusNamespaceNames: {
  name: nsName
}]

// Role assignment for Storage Account (Blob) - Managed Identity
resource storageRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for i in range(0, length(storageAccountNames)): if (!empty(managedIdentityPrincipalId)) {
  name: guid(storageAccounts[i].id, managedIdentityPrincipalId, storageRoleDefinitionId) // Use managed identity ID
  scope: storageAccounts[i]
  properties: {
    roleDefinitionId: resourceId('Microsoft.Authorization/roleDefinitions', storageRoleDefinitionId)
    principalId: managedIdentityPrincipalId // Use managed identity ID
    principalType: 'ServicePrincipal' // Managed Identity is a Service Principal
  }
}]

// Role assignment for Storage Account (Blob) - User Identity
resource storageRoleAssignment_User 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for i in range(0, length(storageAccountNames)): if (!empty(userIdentityPrincipalId)) {
  name: guid(storageAccounts[i].id, userIdentityPrincipalId, storageRoleDefinitionId)
  scope: storageAccounts[i]
  properties: {
    roleDefinitionId: resourceId('Microsoft.Authorization/roleDefinitions', storageRoleDefinitionId)
    principalId: userIdentityPrincipalId // Use user identity ID
    principalType: 'User' // User Identity is a User Principal
  }
}]

// Role assignment for Storage Account (Table) - Managed Identity
resource tableRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for i in range(0, length(storageAccountNames)): if (!empty(managedIdentityPrincipalId)) {
  name: guid(storageAccounts[i].id, managedIdentityPrincipalId, tableRoleDefinitionId) // Use managed identity ID
  scope: storageAccounts[i]
  properties: {
    roleDefinitionId: resourceId('Microsoft.Authorization/roleDefinitions', tableRoleDefinitionId)
    principalId: managedIdentityPrincipalId // Use managed identity ID
    principalType: 'ServicePrincipal' // Managed Identity is a Service Principal
  }
}]

// Role assignment for Storage Account (Table) - User Identity
resource tableRoleAssignment_User 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for i in range(0, length(storageAccountNames)): if (!empty(userIdentityPrincipalId)) {
  name: guid(storageAccounts[i].id, userIdentityPrincipalId, tableRoleDefinitionId)
  scope: storageAccounts[i]
  properties: {
    roleDefinitionId: resourceId('Microsoft.Authorization/roleDefinitions', tableRoleDefinitionId)
    principalId: userIdentityPrincipalId // Use user identity ID
    principalType: 'User' // User Identity is a User Principal
  }
}]

// Role assignment for Application Insights - Managed Identity
resource appInsightsRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(appInsightsName) && !empty(managedIdentityPrincipalId)) {
  name: guid(applicationInsights.id, managedIdentityPrincipalId, monitoringRoleDefinitionId) // Use managed identity ID
  scope: applicationInsights
  properties: {
    roleDefinitionId: resourceId('Microsoft.Authorization/roleDefinitions', monitoringRoleDefinitionId)
    principalId: managedIdentityPrincipalId // Use managed identity ID
    principalType: 'ServicePrincipal' // Managed Identity is a Service Principal
  }
}

// Role assignment for Application Insights - User Identity
resource appInsightsRoleAssignment_User 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(appInsightsName) && !empty(userIdentityPrincipalId)) {
  name: guid(applicationInsights.id, userIdentityPrincipalId, monitoringRoleDefinitionId)
  scope: applicationInsights
  properties: {
    roleDefinitionId: resourceId('Microsoft.Authorization/roleDefinitions', monitoringRoleDefinitionId)
    principalId: userIdentityPrincipalId // Use user identity ID
    principalType: 'User' // User Identity is a User Principal
  }
}

// Role assignment for Application Insights - Managed Identity
resource keyVaultRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(keyVaultName) && !empty(managedIdentityPrincipalId)) {
  name: guid(keyVault.id, managedIdentityPrincipalId, keyVaultSecretRoleDefinitionId) // Use managed identity ID
  scope: keyVault
  properties: {
    roleDefinitionId: resourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretRoleDefinitionId)
    principalId: managedIdentityPrincipalId // Use managed identity ID
    principalType: 'ServicePrincipal' // Managed Identity is a Service Principal
  }
}

// Role assignment for Key Vault - User Identity
resource keyVaultRoleAssignment_User 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(keyVaultName) && !empty(userIdentityPrincipalId)) {
  name: guid(keyVault.id, userIdentityPrincipalId, keyVaultSecretRoleDefinitionId)
  scope: keyVault
  properties: {
    roleDefinitionId: resourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretRoleDefinitionId)
    principalId: userIdentityPrincipalId // Use user identity ID
    principalType: 'User' // User Identity is a User Principal
  }
}

// Role assignment for Service Bus - Managed Identity
resource serviceBusRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for i in range(0, length(serviceBusNamespaceNames)): if (!empty(managedIdentityPrincipalId)) {
  name: guid(serviceBusNamespaces[i].id, managedIdentityPrincipalId, serviceBusRoleDefinitionId) // Use managed identity ID
  scope: serviceBusNamespaces[i]
  properties: {
    roleDefinitionId: resourceId('Microsoft.Authorization/roleDefinitions', serviceBusRoleDefinitionId)
    principalId: managedIdentityPrincipalId // Use managed identity ID
    principalType: 'ServicePrincipal' // Managed Identity is a Service Principal
  }
}]

// Role assignment for Service Bus - User Identity
resource serviceBusRoleAssignment_User 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for i in range(0, length(serviceBusNamespaceNames)): if (!empty(userIdentityPrincipalId)) {
  name: guid(serviceBusNamespaces[i].id, userIdentityPrincipalId, serviceBusRoleDefinitionId)
  scope: serviceBusNamespaces[i]
  properties: {
    roleDefinitionId: resourceId('Microsoft.Authorization/roleDefinitions', serviceBusRoleDefinitionId)
    principalId: userIdentityPrincipalId // Use user identity ID
    principalType: 'User' // User Identity is a User Principal
  }
}]
