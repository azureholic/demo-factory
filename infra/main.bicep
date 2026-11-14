targetScope = 'resourceGroup'

@description('Environment name used as resource prefix')
param environmentName string

@description('Primary Azure region (EU region recommended for Microsoft Foundry data residency, §3.2)')
param location string = resourceGroup().location

@allowed([
  'lean'
  'reference'
])
@description('Deployment profile (§3.5): lean (single Container App, SQL inbox, in-proc SignalR) or reference (3 Container Apps, Service Bus, Azure SignalR)')
param deploymentProfile string = 'lean'

@description('Microsoft Entra ID Object ID of the deploying admin user or principal for Entra-only SQL & DTS dashboard RBAC')
param principalId string

@description('Microsoft Entra ID display name / UPN of the deploying admin')
param principalName string

@allowed([
  'User'
  'Group'
  'Application'
])
param principalType string = 'User'

@description('GitHub login of the personal account owning managed demo repositories (§1)')
param ownerLogin string = 'azureholic'

var uniqueHash = take(uniqueString(subscription().id, resourceGroup().id, environmentName), 8)
var prefix = 'df-${toLower(environmentName)}-${uniqueHash}'
var isReference = deploymentProfile == 'reference'

// User-Assigned Managed Identity for all factory Azure connections (zero connection-string secrets, §3.4)
resource factoryIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${prefix}-uami'
  location: location
}

// Log Analytics & Application Insights (OpenTelemetry target, §3.4)
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-logs'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${prefix}-appi'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// Azure Container Registry (§3.4)
resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: 'df${uniqueHash}acr'
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
  }
}

var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
resource acrPullAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, factoryIdentity.id, acrPullRoleId)
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: factoryIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Azure Key Vault (RBAC, non-exportable GitHub App key + refresh-token wrap key, §3.4 & §10.1)
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: take('df-kv-${uniqueHash}', 24)
  location: location
  properties: {
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    sku: {
      family: 'A'
      name: 'standard'
    }
  }
}

resource githubAppSigningKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: keyVault
  name: 'github-app-jwt-signing'
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: [
      'sign'
      'verify'
    ]
    attributes: {
      enabled: true
      exportable: false
    }
  }
}

resource tokenWrapKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: keyVault
  name: 'github-refresh-token-wrap'
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: [
      'encrypt'
      'decrypt'
      'wrapKey'
      'unwrapKey'
    ]
    attributes: {
      enabled: true
      exportable: false
    }
  }
}

var kvCryptoUserRoleId = '12338af0-0e69-4776-bea7-57ae8d297424'
resource kvCryptoRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, factoryIdentity.id, kvCryptoUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvCryptoUserRoleId)
    principalId: factoryIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Azure Blob Storage for gate evidence snapshots & PM agent traces (Entra-only, shared key disabled, §3.4)
resource evidenceStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'dfev${uniqueHash}st'
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    allowSharedKeyAccess: false
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

var blobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
resource blobRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(evidenceStorage.id, factoryIdentity.id, blobDataContributorRoleId)
  scope: evidenceStorage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobDataContributorRoleId)
    principalId: factoryIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Azure SQL Server & Serverless Database — Unconditional Microsoft Entra-only authentication (§3.4)
resource sqlServer 'Microsoft.Sql/servers@2022-05-01-preview' = {
  name: '${prefix}-sql'
  location: location
  properties: {
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: principalType
      login: principalName
      sid: principalId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
    minimalTlsVersion: '1.2'
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2022-05-01-preview' = {
  parent: sqlServer
  name: 'demofactorydb'
  location: location
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 2
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    autoPauseDelay: 60
    minCapacity: json('0.5')
  }
}

resource sqlFirewallAzure 'Microsoft.Sql/servers/firewallRules@2022-05-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

var sqlConnectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDatabase.name};Authentication=Active Directory Managed Identity;User Id=${factoryIdentity.properties.clientId};Encrypt=True;TrustServerCertificate=False;'

// Azure Durable Task Scheduler + Task Hub (§3.2, §3.4)
resource scheduler 'Microsoft.DurableTask/schedulers@2025-11-01' = {
  name: '${prefix}-dts'
  location: location
  properties: {
    sku: {
      name: 'Consumption'
    }
    ipAllowlist: [
      '0.0.0.0/0'
    ]
  }
}

resource taskHub 'Microsoft.DurableTask/schedulers/taskHubs@2025-11-01' = {
  parent: scheduler
  name: 'default'
}

var durableTaskDataContributorRoleId = '0ad04412-c4d5-4796-b79c-f76d14c8d402'

resource dtsWorkerRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(scheduler.id, factoryIdentity.id, durableTaskDataContributorRoleId)
  scope: scheduler
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', durableTaskDataContributorRoleId)
    principalId: factoryIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource dtsDeveloperDashboardRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId) && principalType == 'User') {
  name: guid(scheduler.id, principalId, durableTaskDataContributorRoleId)
  scope: scheduler
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', durableTaskDataContributorRoleId)
    principalId: principalId
    principalType: 'User'
  }
}

var dtsConnectionString = 'Endpoint=${scheduler.properties.endpoint};TaskHub=${taskHub.name};Authentication=ManagedIdentity;ClientID=${factoryIdentity.properties.clientId}'

// Microsoft Foundry Account & Project (EU region for PM agent data residency, §3.2 & §3.4)
resource foundryAccount 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: '${prefix}-foundry'
  location: location
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    customSubDomainName: '${prefix}-foundry'
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

var cognitiveServicesUserRoleId = 'a97b65f3-24c7-4388-baec-2e87135dc908'
resource foundryRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundryAccount.id, factoryIdentity.id, cognitiveServicesUserRoleId)
  scope: foundryAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUserRoleId)
    principalId: factoryIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Reference-profile-only resources: Service Bus & Azure SignalR Service (§3.4, §3.5)
resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = if (isReference) {
  name: '${prefix}-sb'
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
  }
}

resource webhookQueue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = if (isReference) {
  parent: serviceBus
  name: 'github-webhooks'
}

resource signalR 'Microsoft.SignalRService/signalR@2023-02-01' = if (isReference) {
  name: '${prefix}-signalr'
  location: location
  sku: {
    name: 'Standard_S1'
    capacity: 1
  }
  properties: {
    disableLocalAuth: true
    features: [
      {
        flag: 'ServiceMode'
        value: 'Default'
      }
    ]
  }
}

// Container Apps Environment & Container Apps (§3.4, §3.5)
resource containerAppsEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${prefix}-cae'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

var commonEnvVars = [
  {
    name: 'AZURE_CLIENT_ID'
    value: factoryIdentity.properties.clientId
  }
  {
    name: 'ConnectionStrings__DefaultConnection'
    value: sqlConnectionString
  }
  {
    name: 'DURABLE_TASK_SCHEDULER_CONNECTION_STRING'
    value: dtsConnectionString
  }
  {
    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
    value: appInsights.properties.ConnectionString
  }
  {
    name: 'Factory__Profile'
    value: isReference ? 'Reference' : 'Lean'
  }
  {
    name: 'Factory__OwnerLogin'
    value: ownerLogin
  }
  {
    name: 'Factory__SandboxMode'
    value: 'false'
  }
  {
    name: 'Factory__ManagedIdentityClientId'
    value: factoryIdentity.properties.clientId
  }
  {
    name: 'Factory__EvidenceBlobServiceUri'
    value: evidenceStorage.properties.primaryEndpoints.blob
  }
  {
    name: 'GitHub__ManagedIdentityClientId'
    value: factoryIdentity.properties.clientId
  }
  {
    name: 'GitHub__KeyVaultSigningKeyId'
    value: githubAppSigningKey.properties.keyUriWithVersion
  }
  {
    name: 'GitHub__KeyVaultWrapKeyId'
    value: tokenWrapKey.properties.keyUriWithVersion
  }
  {
    name: 'Foundry__ManagedIdentityClientId'
    value: factoryIdentity.properties.clientId
  }
  {
    name: 'Foundry__ProjectEndpoint'
    value: foundryAccount.properties.endpoint
  }
]

// Primary Container App (`factory-api`; in Lean profile hosts SPA + API + Orchestrator in 1 replica)
resource factoryApiApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-api'
  location: location
  tags: {
    'azd-service-name': 'factory-api'
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${factoryIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnv.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: [
        {
          server: acr.properties.loginServer
          identity: factoryIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'factory-api'
          image: 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: commonEnvVars
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: isReference ? 5 : 1
      }
    }
  }
}

// Reference profile dedicated Orchestrator worker (no ingress)
resource factoryOrchestratorApp 'Microsoft.App/containerApps@2024-03-01' = if (isReference) {
  name: '${prefix}-orchestrator'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${factoryIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnv.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [
        {
          server: acr.properties.loginServer
          identity: factoryIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'factory-orchestrator'
          image: 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: commonEnvVars
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 4
      }
    }
  }
}

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = acr.properties.loginServer
output SERVICE_API_NAME string = factoryApiApp.name
output SERVICE_API_URI string = 'https://${factoryApiApp.properties.configuration.ingress.fqdn}'
output SQL_SERVER string = sqlServer.name
output SQL_DATABASE string = sqlDatabase.name
output SQL_GRANT_DDLADMIN string = 'true'
output DURABLE_TASK_SCHEDULER_ENDPOINT string = scheduler.properties.endpoint
output FOUNDRY_ENDPOINT string = foundryAccount.properties.endpoint
