#Requires -Version 7.0
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
& (Join-Path $PSScriptRoot 'generate-icon.ps1')
$publish = Join-Path $PSScriptRoot ('artifacts\publish-' + [Guid]::NewGuid().ToString('N'))
dotnet publish (Join-Path $PSScriptRoot 'managed\RegistryEditor.csproj') `
    -c $Configuration -r $RuntimeIdentifier --self-contained true -o $publish
$output = Join-Path $PSScriptRoot "artifacts\$RuntimeIdentifier"
# Replace only this bundle so stale files from older publishes are not carried forward.
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
Move-Item -LiteralPath $publish -Destination $output
