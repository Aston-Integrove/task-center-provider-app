#!/usr/bin/env pwsh
# Resolves the deploying principal (Entra user or service principal) into azd env values that
# main.bicep needs for the Key Vault role assignment. Idempotent.
$ErrorActionPreference = 'Stop'

function Get-AzdValue([string]$name) {
    $line = azd env get-values | Where-Object { $_ -like "$name=*" } | Select-Object -First 1
    if ($line) { return ($line -replace "^$name=", '').Trim('"') }
    return $null
}

$id = Get-AzdValue 'AZURE_PRINCIPAL_ID'
$name = Get-AzdValue 'AZURE_PRINCIPAL_NAME'
$type = Get-AzdValue 'AZURE_PRINCIPAL_TYPE'

if (-not $id -or -not $name -or -not $type) {
    $account = az account show --query user -o json | ConvertFrom-Json
    if ($account.type -eq 'user') {
        $id = az ad signed-in-user show --query id -o tsv
        $name = $account.name
        $type = 'User'
    } else {
        # Service principal (CI): the account name is the appId.
        $sp = az ad sp show --id $account.name -o json | ConvertFrom-Json
        $id = $sp.id
        $name = $sp.displayName
        $type = 'ServicePrincipal'
    }
    azd env set AZURE_PRINCIPAL_ID $id
    azd env set AZURE_PRINCIPAL_NAME $name
    azd env set AZURE_PRINCIPAL_TYPE $type
    Write-Host "Deploying principal: $name ($type)"
} else {
    Write-Host "Deploying principal already resolved: $name ($type)"
}
