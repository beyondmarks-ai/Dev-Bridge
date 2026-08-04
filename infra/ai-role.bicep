@description('Existing Microsoft Foundry/Azure AI account name.')
param aiFoundryAccountName string

@description('Managed identity object ID that may perform model inference.')
param principalId string

resource aiFoundry 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: aiFoundryAccountName
}

resource aiInferenceUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiFoundry.id, principalId, 'openai-user')
  scope: aiFoundry
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  }
}
