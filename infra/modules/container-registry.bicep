param name string
param location string
param tags object = {}

@description('Principal that may pull images (user-assigned identity of the container app).')
param pullPrincipalId string

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: name
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
  }
}

var acrPullRole = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource pull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, pullPrincipalId, acrPullRole)
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRole)
    principalId: pullPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output name string = acr.name
output loginServer string = acr.properties.loginServer
