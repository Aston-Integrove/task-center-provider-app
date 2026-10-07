param serverName string
param databaseName string = 'tcp'
param location string
param tags object = {}

@description('Entra admin login (UPN or app display name) of the deploying principal.')
param adminLogin string
param adminObjectId string

@allowed(['User', 'Application'])
param adminPrincipalType string = 'User'

resource server 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: serverName
  location: location
  tags: tags
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: adminPrincipalType
      login: adminLogin
      sid: adminObjectId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
  }
}

resource db 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: server
  name: databaseName
  location: location
  tags: tags
  sku: { name: 'Basic', tier: 'Basic', capacity: 5 }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: 2147483648
    requestedBackupStorageRedundancy: 'Local'
  }
}

// Prototype simplification (documented in plan 001): allow Azure services, i.e. Container Apps egress.
resource allowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: server
  name: 'AllowAllWindowsAzureIps'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

output serverName string = server.name
output fqdn string = server.properties.fullyQualifiedDomainName
output databaseName string = db.name
