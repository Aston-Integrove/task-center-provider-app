targetScope = 'subscription'

@minLength(1)
@maxLength(20)
@description('Name of the azd environment, used to derive resource names.')
param environmentName string

@description('Azure region. Default is South Africa North.')
param location string = 'southafricanorth'

@description('Object id of the deploying principal (azd sets AZURE_PRINCIPAL_ID).')
param principalId string

@allowed(['User', 'ServicePrincipal'])
param principalType string = 'User'

@allowed(['Basic', 'IasOidc'])
@description('"Open in App" sign-in: Basic (admin credentials) or IasOidc (SAP IAS login). The IAS client secret is the Key Vault secret ias-oidc-client-secret.')
param appAuth string = 'Basic'

@description('IAS tenant URL, e.g. https://<tenant>.accounts.ondemand.com (only used with IasOidc).')
param iasAuthority string = ''

@description('Client ID of the IAS application (only used with IasOidc).')
param iasClientId string = ''

@description('Keep 1 while the destination is enabled in Task Center; 0 otherwise.')
param minReplicas int = 1

@description('E-mail for the monthly budget alert (USD 25 by default). Empty = no budget is created.')
param budgetContactEmail string = ''

@description('Monthly budget amount; constitution V caps an environment at 25.')
param budgetAmount int = 25

@description('Resource group name. Empty = rg-<environmentName>.')
param resourceGroupName string = ''

@description('true = the resource group already exists (e.g. created by an admin); it is used as is and not created or re-tagged. It must be in the same region as "location".')
param useExistingResourceGroup bool = false

var tags = { 'azd-env-name': environmentName, app: 'tc-provider' }
var token = toLower(uniqueString(subscription().id, environmentName, location))
var rgName = empty(resourceGroupName) ? 'rg-${environmentName}' : resourceGroupName

resource rgCreate 'Microsoft.Resources/resourceGroups@2024-03-01' = if (!useExistingResourceGroup) {
  name: rgName
  location: location
  tags: tags
}

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' existing = {
  name: rgName
}

module logs 'modules/log-analytics.bicep' = {
  scope: rg
  name: 'logs'
  dependsOn: [rgCreate]
  params: { name: 'log-${token}', location: location, tags: tags }
}

module pullIdentity 'modules/identity.bicep' = {
  scope: rg
  name: 'pull-identity'
  dependsOn: [rgCreate]
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
  dependsOn: [rgCreate]
  params: {
    name: 'kv-${take(token, 20)}'
    location: location
    tags: tags
    deployerPrincipalId: principalId
    deployerPrincipalType: principalType
  }
}

// Holds the SQLite database file (Azure Files share mounted into the container app).
module storage 'modules/storage.bicep' = {
  scope: rg
  name: 'storage'
  dependsOn: [rgCreate]
  params: { name: 'st${take(token, 22)}', location: location, tags: tags }
}

module env 'modules/container-apps-env.bicep' = {
  scope: rg
  name: 'cae'
  params: {
    name: 'cae-${token}'
    location: location
    tags: tags
    logAnalyticsName: logs.outputs.name
    storageAccountName: storage.outputs.name
    shareName: storage.outputs.shareName
  }
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
    storageMountName: env.outputs.storageMountName
    publicBaseUrl: 'https://${appName}.${env.outputs.defaultDomain}'
    minReplicas: minReplicas
    extraEnv: appAuth == 'IasOidc' ? [
      { name: 'App__Auth', value: 'IasOidc' }
      { name: 'App__Oidc__Authority', value: iasAuthority }
      { name: 'App__Oidc__ClientId', value: iasClientId }
    ] : []
  }
}

module budget 'modules/budget.bicep' = if (!empty(budgetContactEmail)) {
  scope: rg
  name: 'budget'
  dependsOn: [rgCreate]
  params: { name: 'budget-${environmentName}', amount: budgetAmount, contactEmails: [budgetContactEmail] }
}

output AZURE_LOCATION string = location
output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = registry.outputs.loginServer
output AZURE_CONTAINER_REGISTRY_NAME string = registry.outputs.name
output AZURE_KEY_VAULT_NAME string = vault.outputs.name
output AZURE_KEY_VAULT_URI string = vault.outputs.uri
output AZURE_STORAGE_ACCOUNT string = storage.outputs.name
output APP_NAME string = app.outputs.name
output APP_FQDN string = app.outputs.fqdn
output PUBLIC_BASE_URL string = 'https://${app.outputs.fqdn}'
