#!/usr/bin/env pwsh
# Post-provision step Bicep cannot do (plan 001): seed Key Vault secrets (random values / RSA signing key),
# only when absent. The database is a SQLite file on an Azure Files share, so there is no database user to create.
# Idempotent: a second run changes nothing. Secret VALUES are never printed.
# Prerequisites: az CLI (logged in), azd, pwsh 7.
$ErrorActionPreference = 'Stop'

function Get-AzdValue([string]$name) {
    $line = azd env get-values | Where-Object { $_ -like "$name=*" } | Select-Object -First 1
    if (-not $line) { throw "azd env value $name is missing" }
    return ($line -replace "^$name=", '').Trim('"')
}

$vault = Get-AzdValue 'AZURE_KEY_VAULT_NAME'

# --- Key Vault secrets --------------------------------------------------------------------------
function Test-Secret([string]$name) {
    az keyvault secret show --vault-name $vault --name $name --query id -o tsv 2>$null | Out-Null
    return ($LASTEXITCODE -eq 0)
}

function New-RandomSecret {
    $bytes = [byte[]]::new(32)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

foreach ($name in 'oauth-client-tc-tech', 'oauth-client-tc-pp', 'oauth-client-ips-scim', 'admin-password') {
    if (Test-Secret $name) { Write-Host "secret $name exists - skipped"; continue }
    $tmp = New-TemporaryFile
    try {
        Set-Content -Path $tmp -Value (New-RandomSecret) -NoNewline
        az keyvault secret set --vault-name $vault --name $name --file $tmp -o none
        Write-Host "secret $name created"
    } finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
}

if (Test-Secret 'token-signing-key') {
    Write-Host 'secret token-signing-key exists - skipped'
} else {
    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    $tmp = New-TemporaryFile
    try {
        Set-Content -Path $tmp -Value $rsa.ExportPkcs8PrivateKeyPem() -NoNewline
        az keyvault secret set --vault-name $vault --name 'token-signing-key' --file $tmp -o none
        Write-Host 'secret token-signing-key created'
    } finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue; $rsa.Dispose() }
}

Write-Host ''
Write-Host "Secrets live in Key Vault '$vault'. Read one with:"
Write-Host "  az keyvault secret show --vault-name $vault --name oauth-client-tc-tech --query value -o tsv"
