@description('Short lowercase prefix used for globally unique Azure resource names.')
@minLength(3)
@maxLength(12)
param resourcePrefix string = 'devbridge'

@description('Azure region for all resources.')
param location string = resourceGroup().location

@secure()
@description('Secret required by the PC dashboard to create test sessions.')
param developerAccessKey string

@description('Resource group containing the existing Microsoft Foundry/Azure AI account.')
param aiFoundryResourceGroupName string

@description('Existing Microsoft Foundry/Azure AI account name.')
param aiFoundryAccountName string

@description('Inference endpoint for the existing Microsoft Foundry/Azure AI account.')
param aiFoundryEndpoint string

@description('Deployed model name used for issue analysis.')
param aiModelDeployment string

var suffix = uniqueString(subscription().id, resourceGroup().id)
var storageName = take(toLower('${resourcePrefix}${suffix}'), 24)
var appName = toLower('${resourcePrefix}-${suffix}')
var planName = '${resourcePrefix}-plan'

resource storage 'Microsoft.Storage/storageAccounts@2025-06-01' = {
  name: storageName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource artifacts 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-06-01' = {
  name: '${storage.name}/default/devbridge-artifacts'
  properties: { publicAccess: 'None' }
}

resource diagnosticsTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2025-06-01' = {
  name: '${storage.name}/default/DevBridgeDiagnostics'
  properties: {}
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${resourcePrefix}-logs'
  location: location
  properties: {
    retentionInDays: 30
    features: { enableLogAccessUsingOnlyResourcePermissions: true }
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${resourcePrefix}-insights'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    IngestionMode: 'LogAnalytics'
    WorkspaceResourceId: logs.id
  }
}

resource plan 'Microsoft.Web/serverfarms@2025-03-01' = {
  name: planName
  location: location
  sku: { name: 'B1', tier: 'Basic', capacity: 1 }
  kind: 'linux'
  properties: { reserved: true }
}

resource app 'Microsoft.Web/sites@2025-03-01' = {
  name: appName
  location: location
  kind: 'app,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
      webSocketsEnabled: true
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
        { name: 'DevBridge__PublicBaseUrl', value: 'https://${appName}.azurewebsites.net' }
        { name: 'DevBridge__DeveloperAccessKey', value: developerAccessKey }
        { name: 'DevBridge__ArtifactPath', value: '/home/data/artifacts' }
        { name: 'DevBridge__StorageAccountName', value: storage.name }
        { name: 'DevBridge__DiagnosticTableName', value: 'DevBridgeDiagnostics' }
        { name: 'DevBridge__StorageContainerName', value: 'devbridge-artifacts' }
        { name: 'DevBridge__MaximumAttachmentMegabytes', value: '100' }
        { name: 'DevBridge__AiEndpoint', value: aiFoundryEndpoint }
        { name: 'DevBridge__AiModelDeployment', value: aiModelDeployment }
        { name: 'DevBridge__AiMaximumEvidenceEvents', value: '40' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
        { name: 'WEBSITES_CONTAINER_START_TIME_LIMIT', value: '300' }
      ]
    }
  }
}

resource tableContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, app.id, 'table-contributor')
  scope: storage
  properties: {
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3')
  }
}

resource blobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, app.id, 'blob-contributor')
  scope: storage
  properties: {
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}

module aiInferenceRole 'ai-role.bicep' = {
  name: 'ai-inference-role'
  scope: resourceGroup(aiFoundryResourceGroupName)
  params: {
    aiFoundryAccountName: aiFoundryAccountName
    principalId: app.identity.principalId
  }
}

output webAppName string = app.name
output publicUrl string = 'https://${app.properties.defaultHostName}'
output storageAccountName string = storage.name
output applicationInsightsName string = insights.name
output aiModelDeployment string = aiModelDeployment
