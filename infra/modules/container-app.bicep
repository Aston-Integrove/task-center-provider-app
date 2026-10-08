param name string
param location string
param tags object = {}
param environmentId string
param registryLoginServer string
param pullIdentityId string
param keyVaultName string
param keyVaultUri string
param storageMountName string
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
            { name: 'ConnectionStrings__Database', value: 'Data Source=/data/tcp.db' }
            // WAL needs shared memory, which a network file system (Azure Files / SMB) cannot provide.
            { name: 'Database__JournalMode', value: 'DELETE' }
          ], extraEnv)
          volumeMounts: [
            { volumeName: 'data', mountPath: '/data' }
          ]
          probes: [
            { type: 'Liveness', httpGet: { path: '/healthz', port: targetPort }, periodSeconds: 30 }
            { type: 'Readiness', httpGet: { path: '/healthz/ready', port: targetPort }, periodSeconds: 15, failureThreshold: 6 }
          ]
        }
      ]
      volumes: [
        {
          name: 'data'
          storageType: 'AzureFile'
          storageName: storageMountName
          // uid/gid 1654 = the non-root "app" user in the image. Byte-range locks stay ON so SQLite can lock the file.
          mountOptions: 'dir_mode=0770,file_mode=0660,uid=1654,gid=1654,mfsymlinks'
        }
      ]
      // SQLite allows one writer process: never more than one replica.
      scale: { minReplicas: minReplicas, maxReplicas: 1 }
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
