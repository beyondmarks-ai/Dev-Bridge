param(
    [string] $ResourceGroup = 'devbridge-rg',
    [Parameter(Mandatory = $true)]
    [string] $WebAppName
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repositoryRoot 'src\DevBridge.Server\DevBridge.Server.csproj'
$deployRoot = Join-Path $repositoryRoot '.deploy'
$publishRoot = Join-Path $deployRoot 'publish'
$zipPath = Join-Path $deployRoot 'devbridge.zip'

New-Item -ItemType Directory -Path $deployRoot -Force | Out-Null
dotnet publish $project --configuration Release --output $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
$root = [IO.Path]::GetFullPath($publishRoot).TrimEnd('\')
$archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object {
        $entryName = $_.FullName.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive,
            $_.FullName,
            $entryName,
            [IO.Compression.CompressionLevel]::Optimal
        ) | Out-Null
    }
}
finally {
    $archive.Dispose()
}

az webapp deploy `
    --resource-group $ResourceGroup `
    --name $WebAppName `
    --src-path $zipPath `
    --type zip `
    --clean true `
    --restart true `
    --output json
