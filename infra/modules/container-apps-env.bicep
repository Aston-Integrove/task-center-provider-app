param name string
param location string
param tags object = {}
param logAnalyticsName string
param storageAccountName string
param shareName string

@description('Name the container app uses to refer to the mounted share.')
param storageMountName string = 'tcpdata'

resource account 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource law 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: logAnalyticsName
}

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: law.properties.customerId
        sharedKey: law.listKeys().primarySharedKey
      }
    }
  }
}

resource dataStorage 'Microsoft.App/managedEnvironments/storages@2024-03-01' = {
  parent: env
  name: storageMountName
  properties: {
    azureFile: {
      accountName: account.name
      accountKey: account.listKeys().keys[0].value
      shareName: shareName
      accessMode: 'ReadWrite'
    }
  }
}

output id string = env.id
output storageMountName string = dataStorage.name
output defaultDomain string = env.properties.defaultDomain
