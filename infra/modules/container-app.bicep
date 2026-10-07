param name string
param location string
param tags object = {}
param environmentId string
param registryLoginServer string
param pullIdentityId string
param keyVaultName string
param keyVaultUri string
param sqlFqdn string
param sqlDatabaseName string
param publicBaseUrl string

@description('Bootstrap image; `azd deploy` replaces it with the built image.')
param image string = 'mcr.microsoft.com/k8se/quickstart:latest'
param targetPort int = 8080
param minReplicas int = 1
param extraEnv array = []

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  tags: union(tags, { 'azd-service-name': 'api' })
  identity: {
    type: 'SystemAssigned,UserAssigned'
    userAssignedIdentities: { '${pullIdentityId}': {} }
  }
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: targetPort
        transport: 'auto'
        allowInsecure: false
      }
      registries: [
        { server: registryLoginServer, identity: pullIdentityId }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: image
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: concat([
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'Database__MigrateOnStartup', value: 'true' }
            { name: 'KeyVault__Uri', value: keyVaultUri }
            { name: 'Provider__PublicBaseUrl', value: publicBaseUrl }
            { name: 'OAuth__Issuer', value: publicBaseUrl }
            {
              name: 'ConnectionStrings__Sql'
              value: 'Server=tcp:${sqlFqdn},1433;Database=${sqlDatabaseName};Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;Connect Timeout=30'
            }
          ], extraEnv)
          probes: [
            { type: 'Liveness', httpGet: { path: '/healthz', port: targetPort }, periodSeconds: 30 }
            { type: 'Readiness', httpGet: { path: '/healthz/ready', port: targetPort }, periodSeconds: 15, failureThreshold: 6 }
          ]
        }
      ]
      scale: { minReplicas: minReplicas, maxReplicas: 2 }
    }
  }
}

var secretsUser = '4633458b-17de-408a-b874-0e0b6abdb3b9'

resource kvRead 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(kv.id, app.id, secretsUser)
  scope: kv
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', secretsUser)
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output name string = app.name
output fqdn string = app.properties.configuration.ingress.fqdn
output principalId string = app.identity.principalId
