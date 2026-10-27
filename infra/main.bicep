// Weather images: Azure Function app (Flex Consumption, .NET isolated) + Storage (blobs, queues, tables)
// Deploy with deploy.ps1, or manually:
//   az group create -n rg-weather-images -l westeurope
//   az deployment group create -g rg-weather-images -f infra/main.bicep -p apiKey=<secret>

targetScope = 'resourceGroup'

@description('Azure region. Must support Flex Consumption (az functionapp list-flexconsumption-locations).')
param location string = resourceGroup().location

@description('Short prefix for resource names: 3-9 lowercase letters/digits.')
@minLength(3)
@maxLength(9)
param namePrefix string = 'wximg'

@description('API key that clients must send in the x-api-key header to start a job. Empty = no authentication.')
@secure()
param apiKey string = ''

@description('Flex Consumption runtime version for dotnet-isolated. deploy.ps1 looks up the exact value for the region.')
param dotnetVersion string = '10.0'

@description('Maximum number of instances the app may scale out to.')
@minValue(40)
@maxValue(1000)
param maximumInstanceCount int = 40

@description('Memory per instance in MB.')
@allowed([
  512
  2048
  4096
])
param instanceMemoryMB int = 2048

@description('How long the SAS links returned by the results endpoint stay valid.')
param sasExpiryMinutes int = 1440

// ---------------------------------------------------------------------------------------------
// Names (deterministic per resource group, so re-running the deployment updates the same resources)
// ---------------------------------------------------------------------------------------------
var prefix = toLower(namePrefix)
var token = uniqueString(resourceGroup().id)
var storageAccountName = take('st${prefix}${token}', 24)
var functionAppName = 'func-${prefix}-${token}'
var planName = 'plan-${prefix}-${token}'
var identityName = 'id-${prefix}-${token}'
var logAnalyticsName = 'log-${prefix}-${token}'
var appInsightsName = 'appi-${prefix}-${token}'

var deploymentContainerName = 'app-package'
var imagesContainerName = 'images' // keep in sync with Names.ImagesContainer
var queueNames = [
  'start-jobs' // queue 1: start a job (Names.StartQueue)
  'image-jobs' // queue 2: render one image (Names.ImageQueue)
  'start-jobs-poison' // the Functions runtime moves messages here after 5 failed attempts
  'image-jobs-poison'
]
var tableNames = [
  'jobs' // one row per job (Names.JobsTable)
  'jobimages' // one row per image of a job (Names.JobImagesTable)
]

// Built-in role definitions the app's identity needs on the storage account (no storage keys are used anywhere).
var storageRoles = [
  {
    name: 'Storage Blob Data Owner' // host + deployment package + images + user delegation SAS
    id: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  }
  {
    name: 'Storage Queue Data Contributor' // create / read / delete queue messages
    id: '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  }
  {
    name: 'Storage Table Data Contributor' // job status
    id: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
  }
]

// ---------------------------------------------------------------------------------------------
// Monitoring
// ---------------------------------------------------------------------------------------------
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
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
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// ---------------------------------------------------------------------------------------------
// Storage: blobs (images), queues (jobs), tables (status)
// ---------------------------------------------------------------------------------------------
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    accessTier: 'Hot'
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false // images are served with short-lived SAS links, never publicly
    allowSharedKeyAccess: false // identity-based access only: no account keys
    publicNetworkAccess: 'Enabled'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: deploymentContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource imagesContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: imagesContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource queues 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = [
  for queueName in queueNames: {
    parent: queueService
    name: queueName
  }
]

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource tables 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = [
  for tableName in tableNames: {
    parent: tableService
    name: tableName
  }
]

// ---------------------------------------------------------------------------------------------
// Identity + permissions (created BEFORE the function app so it can start with working access)
// ---------------------------------------------------------------------------------------------
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
}

resource roleAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for role in storageRoles: {
    name: guid(storage.id, identity.id, role.id)
    scope: storage
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role.id)
      principalId: identity.properties.principalId
      principalType: 'ServicePrincipal'
      description: '${role.name} for the weather images function app'
    }
  }
]

// ---------------------------------------------------------------------------------------------
// Function app (Flex Consumption, Linux, .NET isolated)
// ---------------------------------------------------------------------------------------------
resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  kind: 'functionapp'
  sku: {
    tier: 'FlexConsumption'
    name: 'FC1'
  }
  properties: {
    reserved: true // Linux
  }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      minTlsVersion: '1.2'
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: identity.id
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: instanceMemoryMB
      }
      runtime: {
        name: 'dotnet-isolated'
        version: dotnetVersion
      }
    }
  }
  dependsOn: [
    roleAssignments
    deploymentContainer
    imagesContainer
    queues
    tables
  ]

  resource appSettings 'config' = {
    name: 'appsettings'
    properties: {
      // Identity-based connection used by the Functions host, the queue triggers AND the app code.
      AzureWebJobsStorage__accountName: storage.name
      AzureWebJobsStorage__credential: 'managedidentity'
      AzureWebJobsStorage__clientId: identity.properties.clientId
      AzureWebJobsStorage__blobServiceUri: storage.properties.primaryEndpoints.blob
      AzureWebJobsStorage__queueServiceUri: storage.properties.primaryEndpoints.queue
      AzureWebJobsStorage__tableServiceUri: storage.properties.primaryEndpoints.table

      APPLICATIONINSIGHTS_CONNECTION_STRING: appInsights.properties.ConnectionString

      // Application settings (bound to WeatherImagesOptions)
      WeatherImages__ApiKey: apiKey
      WeatherImages__SasExpiryMinutes: string(sasExpiryMinutes)
    }
  }
}

// ---------------------------------------------------------------------------------------------
output functionAppName string = functionApp.name
output functionAppHostName string = functionApp.properties.defaultHostName
output apiBaseUrl string = 'https://${functionApp.properties.defaultHostName}/api'
output storageAccountName string = storage.name
output identityClientId string = identity.properties.clientId
