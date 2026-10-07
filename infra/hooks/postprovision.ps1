#!/usr/bin/env pwsh
# Post-provision steps Bicep cannot do (plan 001):
#   1. let this machine reach Azure SQL (firewall rule "azd-client")
#   2. create the contained DB user for the Container App's managed identity
#   3. seed Key Vault secrets (random values / RSA signing key) - only when absent
# Idempotent: a second run changes nothing. Secret VALUES are never printed.
# Prerequisites: az CLI (logged in), azd, go-sqlcmd (`winget install sqlcmd`), pwsh 7.
$ErrorActionPreference = 'Stop'

function Get-AzdValue([string]$name) {
    $line = azd env get-values | Where-Object { $_ -like "$name=*" } | Select-Object -First 1
    if (-not $line) { throw "azd env value $name is missing" }
    return ($line -replace "^$name=", '').Trim('"')
}

$rg = Get-AzdValue 'AZURE_RESOURCE_GROUP'
$vault = Get-AzdValue 'AZURE_KEY_VAULT_NAME'
$sqlServer = Get-AzdValue 'AZURE_SQL_SERVER'
$sqlFqdn = Get-AzdValue 'AZURE_SQL_FQDN'
$database = Get-AzdValue 'AZURE_SQL_DATABASE'
$appName = Get-AzdValue 'APP_NAME'

# --- 1. SQL firewall for the machine running the hook -------------------------------------------
$ip = (Invoke-RestMethod -Uri 'https://api.ipify.org').Trim()
az sql server firewall-rule create -g $rg -s $sqlServer -n azd-client --start-ip-address $ip --end-ip-address $ip -o none
Write-Host "SQL firewall rule azd-client -> $ip"

# --- 2. DB user for the managed identity --------------------------------------------------------
# CREATE USER ... WITH SID avoids needing Directory Readers on the SQL server identity
# (plan 001 used FROM EXTERNAL PROVIDER; this is equivalent and needs fewer permissions).
$principalObjectId = az containerapp show -g $rg -n $appName --query identity.principalId -o tsv
$clientId = az ad sp show --id $principalObjectId --query appId -o tsv
if (-not $clientId) { throw 'Could not resolve the managed identity client id' }

$sql = @"
SET NOCOUNT ON;
DECLARE @sid varbinary(16) = CONVERT(varbinary(16), CAST('$clientId' AS uniqueidentifier));
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$appName')
BEGIN
    DECLARE @ddl nvarchar(400) = N'CREATE USER [$appName] WITH SID = ' + CONVERT(nvarchar(100), @sid, 1) + N', TYPE = E';
    EXEC (@ddl);
    PRINT 'created user $appName';
END
ELSE PRINT 'user $appName exists';
ALTER ROLE db_datareader ADD MEMBER [$appName];
ALTER ROLE db_datawriter ADD MEMBER [$appName];
ALTER ROLE db_ddladmin ADD MEMBER [$appName];
"@
sqlcmd -S $sqlFqdn -d $database --authentication-method ActiveDirectoryDefault -b -Q $sql
if ($LASTEXITCODE -ne 0) { throw 'sqlcmd failed creating the managed-identity user' }

# --- 3. Key Vault secrets -----------------------------------------------------------------------
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
