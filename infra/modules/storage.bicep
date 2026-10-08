param name string
param location string
param tags object = {}
param shareName string = 'tcp-data'

@description('Share quota in GiB. The SQLite file of this prototype stays far below 1 GiB.')
param shareQuotaGiB int = 5

resource account 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    // Container Apps mounts Azure Files with the account key; there is no identity-based SMB mount for it yet.
    allowSharedKeyAccess: true
    publicNetworkAccess: 'Enabled'
  }
}

resource files 'Microsoft.Storage/storageAccounts/fileServices@2023-05-01' = {
  parent: account
  name: 'default'
}

resource share 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  parent: files
  name: shareName
  properties: {
    enabledProtocols: 'SMB'
    shareQuota: shareQuotaGiB
  }
}

output name string = account.name
output shareName string = share.name
