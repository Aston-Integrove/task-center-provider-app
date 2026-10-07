param name string
param location string
param tags object = {}

@description('Deploying principal; gets Secrets Officer so the postprovision hook can seed secrets.')
param deployerPrincipalId string

@allowed(['User', 'ServicePrincipal'])
param deployerPrincipalType string = 'User'

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
  }
}

var secretsOfficer = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'

resource officer 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId)) {
  name: guid(kv.id, deployerPrincipalId, secretsOfficer)
  scope: kv
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', secretsOfficer)
    principalId: deployerPrincipalId
    principalType: deployerPrincipalType
  }
}

output name string = kv.name
output uri string = kv.properties.vaultUri
output id string = kv.id
