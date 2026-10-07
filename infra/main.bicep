targetScope = 'subscription'

@minLength(1)
@maxLength(20)
@description('Name of the azd environment, used to derive resource names.')
param environmentName string

@description('Azure region. Default is South Africa North.')
param location string = 'southafricanorth'

@description('Object id of the deploying principal (azd sets AZURE_PRINCIPAL_ID).')
param principalId string

@description('Login/UPN (or app display name) of the deploying principal; set by the preprovision hook.')
param principalName string

@allowed(['User', 'ServicePrincipal'])
param principalType string = 'User'

@description('Keep 1 while the destination is enabled in Task Center; 0 otherwise.')
param minReplicas int = 1

@description('E-mail for the monthly budget alert (USD 25 by default). Empty = no budget is created.')
param budgetContactEmail string = ''

@description('Monthly budget amount; constitution V caps an environment at 25.')
param budgetAmount int = 25

var tags = { 'azd-env-name': environmentName, app: 'tc-provider' }
var token = toLower(uniqueString(subscription().id, environmentName, location))

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: tags
}

module logs 'modules/log-analytics.bicep' = {
  scope: rg
  name: 'logs'
  params: { name: 'log-${token}', location: location, tags: tags }
}

module pullIdentity 'modules/identity.bicep' = {
  scope: rg
  name: 'pull-identity'
  params: { name: 'id-acrpull-${token}', location: location, tags: tags }
}

module registry 'modules/container-registry.bicep' = {
  scope: rg
  name: 'registry'
  params: {
    name: 'acr${token}'
    location: location
    tags: tags
    pullPrincipalId: pullIdentity.outputs.principalId
  }
}

module vault 'modules/key-vault.bicep' = {
  scope: rg
  name: 'vault'
  params: {
    name: 'kv-${take(token, 20)}'
    location: location
    tags: tags
    deployerPrincipalId: principalId
    deployerPrincipalType: principalType
  }
}

module sql 'modules/sql.bicep' = {
  scope: rg
  name: 'sql'
  params: {
    serverName: 'sql-${token}'
    location: location
    tags: tags
    adminLogin: principalName
    adminObjectId: principalId
    adminPrincipalType: principalType == 'User' ? 'User' : 'Application'
  }
}

module env 'modules/container-apps-env.bicep' = {
  scope: rg
  name: 'cae'
  params: { name: 'cae-${token}', location: location, tags: tags, logAnalyticsName: logs.outputs.name }
}

var appName = 'ca-tcp-${take(token, 8)}'

module app 'modules/container-app.bicep' = {
  scope: rg
  name: 'app'
  params: {
    name: appName
    location: location
    tags: tags
    environmentId: env.outputs.id
    registryLoginServer: registry.outputs.loginServer
    pullIdentityId: pullIdentity.outputs.resourceId
    keyVaultName: vault.outputs.name
    keyVaultUri: vault.outputs.uri
    sqlFqdn: sql.outputs.fqdn
    sqlDatabaseName: sql.outputs.databaseName
    publicBaseUrl: 'https://${appName}.${env.outputs.defaultDomain}'
    minReplicas: minReplicas
  }
}

module budget 'modules/budget.bicep' = if (!empty(budgetContactEmail)) {
  scope: rg
  name: 'budget'
  params: { name: 'budget-${environmentName}', amount: budgetAmount, contactEmails: [budgetContactEmail] }
}

output AZURE_LOCATION string = location
output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = registry.outputs.loginServer
output AZURE_CONTAINER_REGISTRY_NAME string = registry.outputs.name
output AZURE_KEY_VAULT_NAME string = vault.outputs.name
output AZURE_KEY_VAULT_URI string = vault.outputs.uri
output AZURE_SQL_SERVER string = sql.outputs.serverName
output AZURE_SQL_FQDN string = sql.outputs.fqdn
output AZURE_SQL_DATABASE string = sql.outputs.databaseName
output APP_NAME string = app.outputs.name
output APP_FQDN string = app.outputs.fqdn
output PUBLIC_BASE_URL string = 'https://${app.outputs.fqdn}'
