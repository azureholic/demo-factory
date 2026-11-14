targetScope = 'subscription'

@description('AZD environment name')
param environmentName string

@description('Primary Azure region (EU region recommended for Microsoft Foundry data residency)')
param location string = 'swedencentral'

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

@allowed([
  'lean'
  'reference'
])
@description('Deployment profile: lean (single Container App) or reference (web + orchestrator + Service Bus + Azure SignalR)')
param deploymentProfile string = 'lean'

@description('Microsoft Entra ID Object ID of the deploying admin user or principal')
param principalId string = ''

@description('Microsoft Entra ID display name / UPN of the deploying admin')
param principalName string = ''

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

@description('Container image pushed by azd deploy (preserved across subsequent azd provision runs)')
param webAppImageName string = ''

var regionAbbreviations = {
  swedencentral: 'swc'
  westeurope: 'weu'
  northeurope: 'neu'
  germanywestcentral: 'gwc'
  uksouth: 'uks'
  ukwest: 'ukw'
  francecentral: 'frc'
  switzerlandnorth: 'szn'
  norwayeast: 'noe'
  spaincentral: 'spc'
  italynorth: 'itn'
  polandcentral: 'plc'
  eastus: 'eus'
  eastus2: 'eus2'
  westus2: 'wus2'
  westus3: 'wus3'
  centralus: 'cus'
  northcentralus: 'ncus'
  southcentralus: 'scus'
  canadacentral: 'cac'
  australiaeast: 'aue'
  japaneast: 'jpe'
  southeastasia: 'sea'
}

var normalizedLocation = toLower(replace(location, ' ', ''))
var regionShort = regionAbbreviations[?normalizedLocation] ?? take(normalizedLocation, 4)
var resourceGroupName = 'rg-${toLower(workloadName)}-${toLower(environmentType)}-${regionShort}'

var commonTags = {
  'azd-env-name': environmentName
  workload: workloadName
  environment: environmentType
  region: normalizedLocation
  owner: !empty(principalName) ? principalName : ownerLogin
  managedBy: 'azd-bicep'
}

// WAF/CAF Resource Group (`rg-demofactory-dev-swc`)
resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: commonTags
}

module resources './resources.bicep' = {
  name: 'deploy-${toLower(workloadName)}-${toLower(environmentType)}-${regionShort}'
  scope: rg
  params: {
    environmentName: environmentName
    location: location
    workloadName: workloadName
    workloadShort: workloadShort
    environmentType: environmentType
    regionShort: regionShort
    deploymentProfile: deploymentProfile
    principalId: principalId
    principalType: principalType
    ownerLogin: ownerLogin
    factoryAppSlug: factoryAppSlug
    githubAppId: githubAppId
    vnetAddressPrefix: vnetAddressPrefix
    webAppImageName: webAppImageName
    tags: commonTags
  }
}

output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_LOCATION string = location
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = resources.outputs.AZURE_CONTAINER_REGISTRY_ENDPOINT
output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = resources.outputs.AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID
output MANAGED_IDENTITY_CLIENT_ID string = resources.outputs.MANAGED_IDENTITY_CLIENT_ID
output MANAGED_IDENTITY_NAME string = resources.outputs.MANAGED_IDENTITY_NAME
output SERVICE_WEB_NAME string = resources.outputs.SERVICE_WEB_NAME
output SERVICE_WEB_URI string = resources.outputs.SERVICE_WEB_URI
output SQL_SERVER string = resources.outputs.SQL_SERVER
output SQL_DATABASE string = resources.outputs.SQL_DATABASE
output SQL_PUBLIC_ACCESS string = resources.outputs.SQL_PUBLIC_ACCESS
output SQL_GRANT_DDLADMIN string = resources.outputs.SQL_GRANT_DDLADMIN
output VNET_NAME string = resources.outputs.VNET_NAME
output DURABLE_TASK_SCHEDULER_ENDPOINT string = resources.outputs.DURABLE_TASK_SCHEDULER_ENDPOINT
output FOUNDRY_ENDPOINT string = resources.outputs.FOUNDRY_ENDPOINT
output FOUNDRY_PROJECT_ENDPOINT string = resources.outputs.FOUNDRY_PROJECT_ENDPOINT
output FOUNDRY_MODEL_DEPLOYMENT string = resources.outputs.FOUNDRY_MODEL_DEPLOYMENT
output AZURE_APPLICATION_INSIGHTS_RESOURCE_ID string = resources.outputs.AZURE_APPLICATION_INSIGHTS_RESOURCE_ID
output AZURE_AI_PROJECT_APPINSIGHTS_CONNECTION_NAME string = resources.outputs.AZURE_AI_PROJECT_APPINSIGHTS_CONNECTION_NAME

