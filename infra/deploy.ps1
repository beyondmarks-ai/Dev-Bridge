param(
    [string] $ResourceGroup = 'devbridge-rg',
    [string] $Location = 'centralindia',
    [string] $ResourcePrefix = 'devbridge',
    [Parameter(Mandatory = $true)]
    [string] $AiFoundryResourceGroupName,
    [Parameter(Mandatory = $true)]
    [string] $AiFoundryAccountName,
    [Parameter(Mandatory = $true)]
    [string] $AiFoundryEndpoint,
    [Parameter(Mandatory = $true)]
    [string] $AiModelDeployment,
    [Parameter(Mandatory = $true)]
    [SecureString] $DeveloperAccessKey
)

$ErrorActionPreference = 'Stop'
$template = Join-Path $PSScriptRoot 'main.bicep'
$plainAccessKey = [System.Net.NetworkCredential]::new('', $DeveloperAccessKey).Password

az group create --name $ResourceGroup --location $Location --output none
$deployment = az deployment group create `
    --resource-group $ResourceGroup `
    --template-file $template `
    --parameters resourcePrefix=$ResourcePrefix developerAccessKey=$plainAccessKey `
        aiFoundryResourceGroupName=$AiFoundryResourceGroupName aiFoundryAccountName=$AiFoundryAccountName `
        aiFoundryEndpoint=$AiFoundryEndpoint aiModelDeployment=$AiModelDeployment `
    --query properties.outputs `
    --output json | ConvertFrom-Json

$deployment | ConvertTo-Json -Depth 5
