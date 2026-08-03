param(
    [string] $ResourceGroup = 'devbridge-rg',
    [string] $Location = 'centralindia',
    [string] $ResourcePrefix = 'devbridge',
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
    --query properties.outputs `
    --output json | ConvertFrom-Json

$deployment | ConvertTo-Json -Depth 5
