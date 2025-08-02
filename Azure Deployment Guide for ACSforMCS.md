# Azure Deployment Guide for ACSforMCS

This guide outlines the steps to deploy ACSforMCS to Azure securely and stably.

Replace all appearances of "acsformcs" in the configuration and scripts with your own name.
Replace AgentPhoneNumber in kayvaultsettings with your phone number

## 1. Infrastructure Setup

### Azure App Service

```powershell
# Create a resource group
$resourceGroup = "rg-acsformcs-prod"
$location = "westeurope"
New-AzResourceGroup -Name $resourceGroup -Location $location

# Create an App Service Plan (P1V2 or above recommended for production)
New-AzAppServicePlan -ResourceGroupName $resourceGroup -Name "plan-acsformcs" -Location $location -Tier "PremiumV2" -WorkerSize "Small" -NumberofWorkers 2

# Create the Web App
New-AzWebApp -ResourceGroupName $resourceGroup -Name "app-acsformcs" -Location $location -AppServicePlan "plan-acsformcs"

# Create a Key Vault
New-AzKeyVault -Name "kv-acsformcs" -ResourceGroupName $resourceGroup -Location $location
```
Key Vault for Secrets
```powershell
# Add secrets
$secrets = @{
    "AcsConnectionString" = "your-acs-connection-string"
    "DirectLineSecret" = "your-directline-secret"
    "CognitiveServiceEndpoint" = "your-cognitive-service-endpoint"
}

foreach ($key in $secrets.Keys) {
    $secureValue = ConvertTo-SecureString $secrets[$key] -AsPlainText -Force
    Set-AzKeyVaultSecret -VaultName "kv-acsformcs" -Name $key -SecretValue $secureValue
}
```
## 2. Security Measures
Enable Managed Identity
```powershell
# Enable system-assigned managed identity for the App Service
Set-AzWebApp -ResourceGroupName $resourceGroup -Name "app-acsformcs" -AssignIdentity $true

# Grant the managed identity access to Key Vault secrets
$appIdentity = (Get-AzWebApp -ResourceGroupName $resourceGroup -Name "app-acsformcs").Identity.PrincipalId
Set-AzKeyVaultAccessPolicy -VaultName "kv-acsformcs" -ObjectId $appIdentity -PermissionsToSecrets get,list
```
Configure App Settings with Key Vault References
```powershell
# Configure app settings to use Key Vault references
$settings = @{
    "AcsConnectionString" = "@Microsoft.KeyVault(SecretUri=https://kv-acsformcs.vault.azure.net/secrets/AcsConnectionString/)"
    "DirectLineSecret" = "@Microsoft.KeyVault(SecretUri=https://kv-acsformcs.vault.azure.net/secrets/DirectLineSecret/)"
    "CognitiveServiceEndpoint" = "@Microsoft.KeyVault(SecretUri=https://kv-acsformcs.vault.azure.net/secrets/CognitiveServiceEndpoint/)"
    "AgentPhoneNumber" = "+yourphonenumber" # Not sensitive, can be directly set
    "BaseUri" = "https://app-acsformcs.azurewebsites.net/api/incomingCall"
    "ASPNETCORE_ENVIRONMENT" = "Production"
}

Set-AzWebApp -ResourceGroupName $resourceGroup -Name "app-acsformcs" -AppSettings $settings
```

Configure TLS/SSL
```powershell
# Ensure HTTPS-only
Set-AzWebApp -ResourceGroupName $resourceGroup -Name "app-acsformcs" -HttpsOnly $true

# Set minimum TLS version to 1.2
$apiVersion = "2018-02-01"
$webapp = Get-AzResource -ResourceGroupName $resourceGroup -ResourceName "app-acsformcs" -ResourceType "Microsoft.Web/sites" -ApiVersion $apiVersion
$webapp.Properties.siteConfig.minTlsVersion = "1.2"
$webapp | Set-AzResource -ApiVersion $apiVersion -Force
```
## Application Insights for Monitoring

```powershell
# Create Application Insights resource
New-AzApplicationInsights -ResourceGroupName $resourceGroup -Name "appi-acsformcs" -Location $location -Kind web

# Get the instrumentation key
$instrumentationKey = (Get-AzApplicationInsights -ResourceGroupName $resourceGroup -Name "appi-acsformcs").InstrumentationKey

# Add Application Insights to the web app
$currentSettings = (Get-AzWebApp -ResourceGroupName $resourceGroup -Name "app-acsformcs").SiteConfig.AppSettings
$newSettings = @{}
foreach ($setting in $currentSettings) {
    $newSettings[$setting.Name] = $setting.Value
}
$newSettings["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "InstrumentationKey=$instrumentationKey"

Set-AzWebApp -ResourceGroupName $resourceGroup -Name "app-acsformcs" -AppSettings $newSettings
```


## Deployment Configuration
CI/CD Pipeline with GitHub Actions
Create a .github/workflows/deploy-azure.yml file:
```yaml
name: Deploy to Azure

on:
  push:
    branches: [ main ]
  workflow_dispatch:

jobs:
  build-and-deploy:
    runs-on: ubuntu-latest
    
    steps:
    - uses: actions/checkout@v3
    
    - name: Setup .NET
      uses: actions/setup-dotnet@v3
      with:
        dotnet-version: '9.0.x'
        
    - name: Build and Test
      run: |
        dotnet restore
        dotnet build --configuration Release --no-restore
        dotnet test --configuration Release --no-build
        
    - name: Publish
      run: dotnet publish --configuration Release --output ./publish
      
    - name: Deploy to Azure Web App
      uses: azure/webapps-deploy@v2
      with:
        app-name: 'app-acsformcs'
        publish-profile: ${{ secrets.AZURE_WEBAPP_PUBLISH_PROFILE }}
        package: ./publish
```

## Scaling Configuration
```powershell
# Configure auto-scaling
$rule1 = New-AzAutoscaleRule -MetricName "CpuPercentage" -MetricResourceId "/subscriptions/{subscription-id}/resourceGroups/$resourceGroup/providers/Microsoft.Web/serverFarms/plan-acsformcs" -Operator "GreaterThan" -MetricStatistic "Average" -Threshold 70 -TimeAggregationOperator "Average" -ScaleActionCooldown 00:05:00 -ScaleActionDirection "Increase" -ScaleActionScaleType "ChangeCount" -ScaleActionValue 1

$rule2 = New-AzAutoscaleRule -MetricName "CpuPercentage" -MetricResourceId "/subscriptions/{subscription-id}/resourceGroups/$resourceGroup/providers/Microsoft.Web/serverFarms/plan-acsformcs" -Operator "LessThan" -MetricStatistic "Average" -Threshold 30 -TimeAggregationOperator "Average" -ScaleActionCooldown 00:05:00 -ScaleActionDirection "Decrease" -ScaleActionScaleType "ChangeCount" -ScaleActionValue 1

$profile = New-AzAutoscaleProfile -Name "AutoscaleProfile" -DefaultCapacity 2 -MaximumCapacity 5 -MinimumCapacity 1 -Rule $rule1,$rule2 -RecurrenceFrequency "Week"

Add-AzAutoscaleSetting -ResourceGroupName $resourceGroup -Location $location -Name "app-acsformcs-autoscale" -TargetResourceId "/subscriptions/{subscription-id}/resourceGroups/$resourceGroup/providers/Microsoft.Web/serverFarms/plan-acsformcs" -AutoscaleProfile $profile
```

## Additional Production Readiness Steps
Configure Health Check Endpoint
```powershell
Set-AzWebApp -ResourceGroupName $resourceGroup -Name "app-acsformcs" -HealthCheckPath "/health"
```

Configure Backup
```powershell
$storageAccount = New-AzStorageAccount -ResourceGroupName $resourceGroup -Name "stacsformcsbackup" -Location $location -SkuName "Standard_LRS"
$storageAccountKey = (Get-AzStorageAccountKey -ResourceGroupName $resourceGroup -Name "stacsformcsbackup")[0].Value
$container = New-AzStorageContainer -Name "webappbackups" -Context $storageAccount.Context

$backupConfig = New-Object Microsoft.Azure.Management.WebSites.Models.BackupRequest
$backupConfig.Enabled = $true
$backupConfig.StorageAccountUrl = "https://stacsformcsbackup.blob.core.windows.net/webappbackups"
$backupConfig.BackupSchedule = New-Object Microsoft.Azure.Management.WebSites.Models.BackupSchedule
$backupConfig.BackupSchedule.FrequencyInterval = 1
$backupConfig.BackupSchedule.FrequencyUnit = "Day"
$backupConfig.BackupSchedule.KeepAtLeastOneBackup = $true
$backupConfig.BackupSchedule.RetentionPeriodInDays = 30

Set-AzWebAppBackupConfiguration -ResourceGroupName $resourceGroup -Name "app-acsformcs" -BackupSchedule $backupConfig -StorageAccountUrl $storageAccountKey
```

Configure Azure Front Door for WAF and CDN
```powershell
# Create Azure Front Door profile
New-AzFrontDoor -ResourceGroupName $resourceGroup -Name "fd-acsformcs" -FrontDoorName "acsformcs" -BackendPoolName "appservice" -HealthProbePath "/health" -HostName "app-acsformcs.azurewebsites.net"
```

Configure Alerts
```powershell
# Create an action group for notifications
$actionGroup = New-AzActionGroup -ResourceGroupName $resourceGroup -Name "ag-acsformcs" -ShortName "acsformcs" -Receiver @{
    "Name" = "email"
    "ReceiverType" = "email"
    "EmailAddress" = "admin@example.com"
}

# Create alerts for high CPU, memory, and failed requests
New-AzMetricAlertRule -ResourceGroupName $resourceGroup -Name "alert-highcpu" -TargetResourceId "/subscriptions/{subscription-id}/resourceGroups/$resourceGroup/providers/Microsoft.Web/serverFarms/plan-acsformcs" -MetricName "CpuPercentage" -Operator "GreaterThan" -Threshold 80 -WindowSize "00:05:00" -TimeAggregation "Average" -Action $actionGroup
```

## Post-Deployment Verification
1. Health Check: Verify /health endpoint returns 200 OK
2.  End-to-End Test: Make a test call to confirm the entire flow works
3. Log Analysis: Check Application Insights for any errors or warnings
4. Performance Test: Run basic load tests to ensure stability under load

## Operational Considerations
Monitoring Strategy
* Set up dashboards in Azure Monitor to track:
  * Call success rates
  * Speech recognition accuracy
  * API response times
  * Error rates
Disaster Recovery
* Configure geo-redundant backups
* Document recovery procedures
* Test restoration processes quarterly
Security Auditing
* Enable diagnostic logs
* Set up regular security scanning
* Review access controls monthly

## Cost Optimization
* Review App Service plan sizing after initial load testing
* Configure auto-scaling to balance performance and cost
* Set up budget alerts to prevent unexpected charges
