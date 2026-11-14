targetScope = 'resourceGroup'

@description('AZD environment name used as uniqueness seed')
param environmentName string

@description('Primary Azure region')
param location string = resourceGroup().location

@description('Workload / application name segment in CAF/WAF resource names')
param workloadName string = 'demofactory'

@description('Compact workload abbreviation for 24-character-constrained global resources (Key Vault, Storage)')
param workloadShort string = 'df'

@allowed([
  'dev'
  'test'
  'stage'
  'prod'
])
@description('Lifecycle environment segment in CAF/WAF resource names')
param environmentType string = 'dev'

@description('Abbreviated Azure region code (for example swc for swedencentral)')
param regionShort string = 'swc'

@description('Zero-padded instance number for resource-group-scoped resources')
param instanceNumber string = '001'

@allowed([
  'lean'
  'reference'
])
@description('Deployment profile: lean (single Container App) or reference (web + orchestrator + Service Bus + Azure SignalR)')
param deploymentProfile string = 'lean'

@description('Microsoft Entra ID Object ID of the deploying admin user or principal')
param principalId string = ''

@allowed([
  'User'
  'Group'
  'Application'
])
param principalType string = 'User'

@description('GitHub login of the personal account owning managed demo repositories')
param ownerLogin string = 'azureholic'

@description('GitHub App slug registered for Demo-Factory')
param factoryAppSlug string = 'azureholic-demo-factory'

@description('Numeric GitHub App ID used for JWT signing & pinning demo-factory/review-gate')
param githubAppId string = '5758548'

@description('Virtual Network CIDR prefix for Container Apps Environment and Private Endpoints')
param vnetAddressPrefix string = '10.200.0.0/16'

@description('Standard WAF Operational Excellence resource tags')
param tags object = {}

var uniqueToken = take(uniqueString(subscription().id, resourceGroup().id, environmentName), 5)
var cleanWorkload = replace(toLower(workloadName), '-', '')
var cleanShort = replace(toLower(workloadShort), '-', '')
var cleanEnv = replace(toLower(environmentType), '-', '')
var baseSegment = '${toLower(workloadName)}-${cleanEnv}-${regionShort}'
var isReference = deploymentProfile == 'reference'

var caeSubnetPrefix = '10.200.0.0/23'
var pepSubnetPrefix = '10.200.2.0/24'
var caeSubnetName = 'snet-cae-${baseSegment}-${instanceNumber}'
var pepSubnetName = 'snet-pep-${baseSegment}-${instanceNumber}'

// CAF/WAF Resource Names (<resource-abbrev>-<workload>-<env>-<region>-<instance|token>)
var managedIdentityName = 'id-${baseSegment}-${instanceNumber}'
var vnetName = 'vnet-${baseSegment}-${instanceNumber}'
var logAnalyticsName = 'log-${baseSegment}-${instanceNumber}'
var appInsightsName = 'appi-${baseSegment}-${instanceNumber}'
var containerRegistryName = take('cr${cleanWorkload}${cleanEnv}${regionShort}${uniqueToken}', 50)
var keyVaultName = take('kv-${cleanShort}-${cleanEnv}-${regionShort}-${uniqueToken}', 24)
var storageAccountName = take('st${cleanShort}${cleanEnv}${regionShort}${uniqueToken}', 24)
var sqlServerName = 'sql-${baseSegment}-${uniqueToken}'
var sqlDatabaseName = 'sqldb-${toLower(workloadName)}-${cleanEnv}'
var sqlPepName = 'pep-sql-${baseSegment}-${instanceNumber}'
var sqlPepNicName = 'nic-pep-sql-${baseSegment}-${instanceNumber}'
var kvPepName = 'pep-kv-${baseSegment}-${instanceNumber}'
var kvPepNicName = 'nic-pep-kv-${baseSegment}-${instanceNumber}'
var stPepName = 'pep-st-${baseSegment}-${instanceNumber}'
var stPepNicName = 'nic-pep-st-${baseSegment}-${instanceNumber}'
var dtsName = 'dts-${baseSegment}-${uniqueToken}'
var foundryAccountName = 'aif-${baseSegment}-${uniqueToken}'
var foundryProjectName = 'proj-${baseSegment}'
var serviceBusName = 'sbns-${baseSegment}-${uniqueToken}'
var serviceBusQueueName = 'sbq-github-webhooks'
var signalRName = 'sigr-${baseSegment}-${uniqueToken}'
var containerAppsEnvName = take('cae-${baseSegment}', 32)
var webContainerAppName = take('ca-${toLower(workloadName)}-web-${cleanEnv}-${regionShort}', 32)
var orchContainerAppName = take('ca-${toLower(workloadName)}-orch-${cleanEnv}-${regionShort}', 32)

// Virtual Network (10.200.0.0/16) with delegated Container Apps subnet and Private Endpoint subnet
resource vnet 'Microsoft.Network/virtualNetworks@2024-01-01' = {
  name: vnetName
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [
        vnetAddressPrefix
      ]
    }
    subnets: [
      {
        name: caeSubnetName
        properties: {
          addressPrefix: caeSubnetPrefix
          delegations: [
            {
              name: 'del-aca-environments'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: pepSubnetName
        properties: {
          addressPrefix: pepSubnetPrefix
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

resource caeSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-01-01' existing = {
  parent: vnet
  name: caeSubnetName
}

resource pepSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-01-01' existing = {
  parent: vnet
  name: pepSubnetName
}

// User-Assigned Managed Identity (`id-*`)
resource factoryIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: managedIdentityName
  location: location
  tags: tags
}

// Log Analytics (`log-*`) & Application Insights (`appi-*`)
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// Azure Container Registry (`cr*`)
resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: containerRegistryName
  location: location
  tags: tags
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

// Azure Key Vault (`kv-*`, RBAC, private endpoint only)
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    publicNetworkAccess: 'Disabled'
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
var kvCryptoOfficerRoleId = '14b46e9e-c2b7-41b4-b07b-48a6ebf60603'
resource kvCryptoRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, factoryIdentity.id, kvCryptoUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvCryptoUserRoleId)
    principalId: factoryIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource kvDeveloperCryptoRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId) && principalType == 'User') {
  name: guid(keyVault.id, principalId, kvCryptoOfficerRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvCryptoOfficerRoleId)
    principalId: principalId
    principalType: 'User'
  }
}

// Azure Blob Storage (`st*`, Entra-only, shared key disabled, private endpoint only)
resource evidenceStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    allowSharedKeyAccess: false
    allowBlobPublicAccess: false
    publicNetworkAccess: 'Disabled'
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

// Azure SQL Server (`sql-*`) & Serverless Database (`sqldb-*`) — Unconditional Microsoft Entra-only + Private Endpoint
resource sqlServer 'Microsoft.Sql/servers@2022-05-01-preview' = {
  name: sqlServerName
  location: location
  tags: tags
  properties: {
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'Application'
      login: factoryIdentity.name
      sid: factoryIdentity.properties.principalId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2022-05-01-preview' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: location
  tags: tags
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

// Private DNS Zones & Private Endpoints (`pep-*` + `nic-pep-*`) inside 10.200.2.0/24
var sqlPrivateDnsZoneName = 'privatelink${environment().suffixes.sqlServerHostname}'
resource sqlPrivateDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: sqlPrivateDnsZoneName
  location: 'global'
  tags: tags
}

resource sqlPrivateDnsVnetLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: sqlPrivateDnsZone
  name: 'vnetlink-sql-${baseSegment}'
  location: 'global'
  tags: tags
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet.id
    }
  }
}

resource sqlPrivateEndpoint 'Microsoft.Network/privateEndpoints@2024-01-01' = {
  name: sqlPepName
  location: location
  tags: tags
  dependsOn: [
    vnet
  ]
  properties: {
    customNetworkInterfaceName: sqlPepNicName
    subnet: {
      id: pepSubnet.id
    }
    privateLinkServiceConnections: [
      {
        name: 'plsc-${sqlPepName}'
        properties: {
          privateLinkServiceId: sqlServer.id
          groupIds: [
            'sqlServer'
          ]
        }
      }
    ]
  }
}

resource sqlPrivateDnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-01-01' = {
  parent: sqlPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'sql-privatelink-config'
        properties: {
          privateDnsZoneId: sqlPrivateDnsZone.id
        }
      }
    ]
  }
}

resource kvPrivateDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.vaultcore.azure.net'
  location: 'global'
  tags: tags
}

resource kvPrivateDnsVnetLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: kvPrivateDnsZone
  name: 'vnetlink-kv-${baseSegment}'
  location: 'global'
  tags: tags
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet.id
    }
  }
}

resource kvPrivateEndpoint 'Microsoft.Network/privateEndpoints@2024-01-01' = {
  name: kvPepName
  location: location
  tags: tags
  dependsOn: [
    vnet
  ]
  properties: {
    customNetworkInterfaceName: kvPepNicName
    subnet: {
      id: pepSubnet.id
    }
    privateLinkServiceConnections: [
      {
        name: 'plsc-${kvPepName}'
        properties: {
          privateLinkServiceId: keyVault.id
          groupIds: [
            'vault'
          ]
        }
      }
    ]
  }
}

resource kvPrivateDnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-01-01' = {
  parent: kvPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'kv-privatelink-config'
        properties: {
          privateDnsZoneId: kvPrivateDnsZone.id
        }
      }
    ]
  }
}

var blobPrivateDnsZoneName = 'privatelink.blob.${environment().suffixes.storage}'
resource blobPrivateDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: blobPrivateDnsZoneName
  location: 'global'
  tags: tags
}

resource blobPrivateDnsVnetLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: blobPrivateDnsZone
  name: 'vnetlink-st-${baseSegment}'
  location: 'global'
  tags: tags
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet.id
    }
  }
}

resource blobPrivateEndpoint 'Microsoft.Network/privateEndpoints@2024-01-01' = {
  name: stPepName
  location: location
  tags: tags
  dependsOn: [
    vnet
  ]
  properties: {
    customNetworkInterfaceName: stPepNicName
    subnet: {
      id: pepSubnet.id
    }
    privateLinkServiceConnections: [
      {
        name: 'plsc-${stPepName}'
        properties: {
          privateLinkServiceId: evidenceStorage.id
          groupIds: [
            'blob'
          ]
        }
      }
    ]
  }
}

resource blobPrivateDnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-01-01' = {
  parent: blobPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'blob-privatelink-config'
        properties: {
          privateDnsZoneId: blobPrivateDnsZone.id
        }
      }
    ]
  }
}

var sqlConnectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDatabase.name};Authentication=Active Directory Managed Identity;User Id=${factoryIdentity.properties.clientId};Encrypt=True;TrustServerCertificate=False;'

// Azure Durable Task Scheduler (`dts-*`) + Task Hub
resource scheduler 'Microsoft.DurableTask/schedulers@2025-11-01' = {
  name: dtsName
  location: location
  tags: tags
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

// Microsoft Foundry Account (`aif-*`), Project (`proj-*`), and MAI-Thinking-1 (`orchestrator-model`) Deployment
resource foundryAccount 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: foundryAccountName
  location: location
  tags: tags
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    customSubDomainName: foundryAccountName
    allowProjectManagement: true
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource foundryModelDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: foundryAccount
  name: 'orchestrator-model'
  sku: {
    name: 'GlobalStandard'
    capacity: 50
  }
  properties: {
    model: {
      format: 'Microsoft'
      name: 'MAI-Thinking-1'
      version: '2026-06-01'
    }
    versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
  }
}

resource foundryProject 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  parent: foundryAccount
  name: foundryProjectName
  location: location
  tags: tags
  dependsOn: [
    foundryModelDeployment
  ]
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    displayName: 'Demo-Factory Orchestrator Project'
    description: 'Microsoft Foundry project hosting MAI-Thinking-1 (orchestrator-model) for Demo-Factory'
  }
}

var foundryProjectEndpoint = 'https://${foundryAccount.name}.services.ai.azure.com/api/projects/${foundryProject.name}'

var cognitiveServicesUserRoleId = 'a97b65f3-24c7-4388-baec-2e87135dc908'
var azureAiUserRoleId = '53ca6127-db72-4b80-b1b0-d745d6d5456d'
var cognitiveServicesOpenAiUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'

resource foundryRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundryAccount.id, factoryIdentity.id, cognitiveServicesUserRoleId)
  scope: foundryAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUserRoleId)
    principalId: factoryIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource foundryAiUserRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundryAccount.id, factoryIdentity.id, azureAiUserRoleId)
  scope: foundryAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', azureAiUserRoleId)
    principalId: factoryIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource foundryOpenAiUserRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundryAccount.id, factoryIdentity.id, cognitiveServicesOpenAiUserRoleId)
  scope: foundryAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesOpenAiUserRoleId)
    principalId: factoryIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource foundryDeveloperCogUserRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId) && principalType == 'User') {
  name: guid(foundryAccount.id, principalId, cognitiveServicesUserRoleId)
  scope: foundryAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUserRoleId)
    principalId: principalId
    principalType: 'User'
  }
}

resource foundryDeveloperAiUserRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId) && principalType == 'User') {
  name: guid(foundryAccount.id, principalId, azureAiUserRoleId)
  scope: foundryAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', azureAiUserRoleId)
    principalId: principalId
    principalType: 'User'
  }
}

resource foundryDeveloperOpenAiUserRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId) && principalType == 'User') {
  name: guid(foundryAccount.id, principalId, cognitiveServicesOpenAiUserRoleId)
  scope: foundryAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesOpenAiUserRoleId)
    principalId: principalId
    principalType: 'User'
  }
}

// Reference-profile-only resources: Service Bus (`sbns-*` / `sbq-*`) & SignalR (`sigr-*`)
resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = if (isReference) {
  name: serviceBusName
  location: location
  tags: tags
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
  name: serviceBusQueueName
}

var serviceBusDataOwnerRoleId = '090c5cfd-751d-490a-894a-3ce6f1109419'
resource serviceBusRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (isReference) {
  name: guid(serviceBus.id, factoryIdentity.id, serviceBusDataOwnerRoleId)
  scope: serviceBus
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataOwnerRoleId)
    principalId: factoryIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource signalR 'Microsoft.SignalRService/signalR@2023-02-01' = if (isReference) {
  name: signalRName
  location: location
  tags: tags
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

// VNet-Integrated Container Apps Environment (`cae-*`; public UI ingress + private VNet outbound to SQL/KV/Storage PEPs)
resource containerAppsEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: containerAppsEnvName
  location: location
  tags: tags
  dependsOn: [
    vnet
  ]
  properties: {
    vnetConfiguration: {
      infrastructureSubnetId: caeSubnet.id
      internal: false
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

var baseEnvVars = [
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
    name: 'Factory__FactoryAppSlug'
    value: factoryAppSlug
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
    name: 'Factory__ServiceBusQueueName'
    value: serviceBusQueueName
  }
  {
    name: 'GitHub__AppId'
    value: githubAppId
  }
  {
    name: 'GitHub__AppSlug'
    value: factoryAppSlug
  }
  {
    name: 'GitHub__DemoFactoryIntegrationId'
    value: githubAppId
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
    value: foundryProjectEndpoint
  }
  {
    name: 'Foundry__DefaultDeployment'
    value: foundryModelDeployment.name
  }
]

var referenceEnvVars = isReference
  ? [
      {
        name: 'Factory__ServiceBusFullyQualifiedNamespace'
        value: '${serviceBus!.name}.servicebus.windows.net'
      }
    ]
  : []

var commonEnvVars = concat(baseEnvVars, referenceEnvVars)

// Primary Web UI + API Container App (`ca-demofactory-web-*`; public HTTPS ingress + private VNet outbound)
resource webApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: webContainerAppName
  location: location
  tags: union(tags, {
    'azd-service-name': 'web'
  })
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${factoryIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnv.id
    workloadProfileName: 'Consumption'
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
          name: 'web'
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

// Reference profile dedicated Orchestrator worker (`ca-demofactory-orch-*`; no public ingress)
resource orchestratorApp 'Microsoft.App/containerApps@2024-03-01' = if (isReference) {
  name: orchContainerAppName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${factoryIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnv.id
    workloadProfileName: 'Consumption'
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
          name: 'orchestrator'
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
output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = factoryIdentity.id
output MANAGED_IDENTITY_CLIENT_ID string = factoryIdentity.properties.clientId
output MANAGED_IDENTITY_NAME string = factoryIdentity.name
output SERVICE_WEB_NAME string = webApp.name
output SERVICE_WEB_URI string = 'https://${webApp.properties.configuration.ingress.fqdn}'
output SQL_SERVER string = sqlServer.name
output SQL_DATABASE string = sqlDatabase.name
output SQL_PUBLIC_ACCESS string = 'Disabled'
output SQL_GRANT_DDLADMIN string = 'true'
output VNET_NAME string = vnet.name
output DURABLE_TASK_SCHEDULER_ENDPOINT string = scheduler.properties.endpoint
output FOUNDRY_ENDPOINT string = foundryAccount.properties.endpoint
output FOUNDRY_PROJECT_ENDPOINT string = foundryProjectEndpoint
output FOUNDRY_MODEL_DEPLOYMENT string = foundryModelDeployment.name
