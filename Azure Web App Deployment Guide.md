# Azure Web App Deployment Guide for ACSforMCS

## Table of Contents

1. [Overview](#overview)
2. [Prerequisites](#prerequisites)
3. [Deployment Architecture](#deployment-architecture)
4. [Quick Start Guide](#quick-start-guide)
5. [Detailed Deployment Steps](#detailed-deployment-steps)
   - [Step 1: Create Azure Web App](#step-1-create-azure-web-app)
   - [Step 2: Configure Managed Identity](#step-2-configure-managed-identity)
   - [Step 3: Configure Application Settings](#step-3-configure-application-settings)
   - [Step 4: Update Key Vault Secrets](#step-4-update-key-vault-secrets)
   - [Step 5: Deploy Application Code](#step-5-deploy-application-code)
   - [Step 6: Update Event Grid Configuration](#step-6-update-event-grid-configuration)
6. [Monitoring and Health Checks](#monitoring-and-health-checks)
7. [Security Configuration](#security-configuration)
8. [Testing and Validation](#testing-and-validation)
9. [Debugging Your Azure Web App](#debugging-your-azure-web-app)
10. [Troubleshooting](#troubleshooting)
11. [Maintenance and Operations](#maintenance-and-operations)
12. [Cost Optimization](#cost-optimization)
13. [Conclusion](#conclusion)

---

## Overview

This guide provides step-by-step instructions for deploying the ACSforMCS application as an Azure Web App. The deployment process maintains your existing Azure resources (Key Vault, Azure Communication Services, Cognitive Services) while moving the application from local development to a cloud-hosted environment.

### What This Guide Covers
- Complete Azure Web App deployment process
- Secure Key Vault integration using managed identity
- Event Grid configuration updates
- Comprehensive debugging and monitoring setup
- Production-ready security configurations

### What Changes from Local Development
- **DevTunnel → Azure Web App URL**: Your local tunnel will be replaced with a permanent Azure Web App endpoint
- **Local Hosting → Cloud Hosting**: The application runs in Azure instead of locally
- **Enhanced Security**: Managed identity replaces connection strings for Key Vault access
- **Better Reliability**: Built-in scaling, backup, and monitoring capabilities

### What Stays the Same
- All your Key Vault secrets and configuration
- Azure Communication Services setup
- Cognitive Services integration
- Copilot Studio agent configuration
- The core application functionality

---

## Prerequisites

Before starting the deployment, ensure you have:

- Existing Azure resources from the initial setup:
  - Azure Communication Services (ACS) resource
  - Azure Key Vault with all secrets configured
  - Azure Cognitive Services resource
  - Microsoft Copilot Studio agent with Direct Line channel
- Azure CLI installed and authenticated
- Visual Studio Code or Visual Studio with Azure extensions
- Git repository with your ACSforMCS code

---

## Quick Start Guide

For experienced users who want to deploy quickly, follow these essential steps:

```bash
# 1. Set your variables
$resourceGroup = "rg-your-resource-base-name-prod"
$appName = "app-your-resource-base-name"
$location = "westeurope"
$keyVaultName = "kv-your-resource-base-name"

# 2. Create Web App
az appservice plan create --name "asp-$appName" --resource-group $resourceGroup --location $location --sku S1
az webapp create --name $appName --resource-group $resourceGroup --plan "asp-$appName" --runtime "DOTNET:9.0"

# 3. Enable managed identity and assign Key Vault access
az webapp identity assign --name $appName --resource-group $resourceGroup
$principalId = az webapp identity show --name $appName --resource-group $resourceGroup --query principalId --output tsv
$keyVaultResourceId = az keyvault show --name $keyVaultName --resource-group $resourceGroup --query id --output tsv
az role assignment create --assignee $principalId --role "Key Vault Secrets User" --scope $keyVaultResourceId

# 4. Configure app settings
az webapp config appsettings set --name $appName --resource-group $resourceGroup --settings `
  "ASPNETCORE_ENVIRONMENT=Production" `
  "KeyVault__Endpoint=https://$keyVaultName.vault.azure.net/"

# 5. Update Key Vault BaseUri
Set-AzKeyVaultSecret -VaultName $keyVaultName -Name 'BaseUri-Production' -SecretValue (ConvertTo-SecureString "https://$appName.azurewebsites.net" -AsPlainText -Force)

# 6. Deploy your code (choose one method from Step 5 below)
# 7. Update Event Grid webhook endpoint (see Step 6 below)
```

For detailed explanations and alternative methods, continue reading the full guide.

---

## Detailed Deployment Steps

## Step 1: Create Azure Web App

### Using Azure Portal

1. **Navigate to Azure Portal**
   - Go to [portal.azure.com](https://portal.azure.com)
   - Sign in with your Azure account

2. **Create App Service**
   - Click "Create a resource"
   - Search for "Web App" and select it
   - Click "Create"

3. **Configure Basic Settings**
   ```
   Subscription: [Your subscription]
   Resource Group: [Same as your existing resources, e.g., rg-your-resource-base-name-prod]
   Name: app-your-resource-base-name
   Publish: Code
   Runtime stack: .NET 9
   Operating System: Windows
   Region: [Same as your other resources, e.g., West Europe]
   ```

4. **Configure App Service Plan**
   - Create new or use existing App Service Plan
   - Recommended: Standard S1 or higher for production workloads
   - Enable "Zone redundancy" for high availability (optional)

### Using Azure CLI

```bash
# Set variables
$resourceGroup = "rg-your-resource-base-name-prod"
$appName = "app-your-resource-base-name"
$location = "westeurope"
$planName = "asp-your-resource-base-name"

# Create App Service Plan
az appservice plan create --name $planName --resource-group $resourceGroup --location $location --sku S1

# Create Web App
az webapp create --name $appName --resource-group $resourceGroup --plan $planName --runtime "DOTNET:9.0"
```

## Step 2: Configure Managed Identity

### Enable System-Assigned Managed Identity

1. **In Azure Portal**
   - Navigate to your Web App
   - Go to "Identity" under Settings
   - Turn on "System assigned" identity
   - Click "Save"

2. **Using Azure CLI**
   ```bash
   az webapp identity assign --name $appName --resource-group $resourceGroup
   ```

### Grant Key Vault Access

1. **Get the Web App's Managed Identity Object ID**
   ```bash
   $principalId = az webapp identity show --name $appName --resource-group $resourceGroup --query principalId --output tsv
   ```

2. **Assign Key Vault Secrets User Role (RBAC)**
   
   Since your Key Vault uses RBAC authorization, you need to assign the appropriate role instead of setting access policies:
   
   ```bash
   $keyVaultName = "kv-your-resource-base-name"
   $keyVaultResourceId = az keyvault show --name $keyVaultName --resource-group $resourceGroup --query id --output tsv
   
   # Assign the "Key Vault Secrets User" role to the Web App's managed identity
   az role assignment create --assignee $principalId --role "Key Vault Secrets User" --scope $keyVaultResourceId
   ```

   **Alternative: If you prefer to use the classic access policy model**
   
   If you want to switch back to access policies instead of RBAC, you can disable RBAC on your Key Vault:
   
   ```bash
   # WARNING: This will disable RBAC and switch to access policies
   az keyvault update --name $keyVaultName --resource-group $resourceGroup --enable-rbac-authorization false
   
   # Then set the access policy
   az keyvault set-policy --name $keyVaultName --object-id $principalId --secret-permissions get list
   ```
   
   **Note:** RBAC is the recommended approach for better security and management.

## Step 3: Configure Application Settings

### Set Environment Variables

1. **In Azure Portal**
   - Navigate to your Web App
   - Go to "Configuration" under Settings
   - Add the following Application Settings:

   ```
   ASPNETCORE_ENVIRONMENT = Production
   KeyVault__Endpoint = https://kv-your-resource-base-name.vault.azure.net/
   ```

2. **Using Azure CLI**
   ```bash
   az webapp config appsettings set --name $appName --resource-group $resourceGroup --settings `
     "ASPNETCORE_ENVIRONMENT=Production" `
     "KeyVault__Endpoint=https://kv-your-resource-base-name.vault.azure.net/"
   ```

## Step 4: Update Key Vault Secrets

Update the BaseUri in your Key Vault to point to your new Web App:

```powershell
# Update the Production BaseUri to your Web App URL
Set-AzKeyVaultSecret -VaultName 'kv-your-resource-base-name' -Name 'BaseUri-Production' -SecretValue (ConvertTo-SecureString 'https://app-your-resource-base-name.azurewebsites.net' -AsPlainText -Force)
```

## Step 5: Deploy Application Code

### Option A: Deploy from Visual Studio

1. **Open Visual Studio**
   - Right-click on the ACSforMCS project
   - Select "Publish"
   - Choose "Azure" as target
   - Select "Azure App Service (Windows)"
   - Sign in and select your Web App
   - Click "Publish"

### Option B: Deploy using Azure CLI

1. **Build and Package**
   ```bash
   # Navigate to your project directory
   cd C:\Users\HolgerImbery\GitHub\ACSforMCS
   
   # Build the application
   dotnet build --configuration Release
   
   # Publish the application
   dotnet publish --configuration Release --output ./publish
   
   # Create deployment package
   Compress-Archive -Path ./publish/* -DestinationPath deployment.zip -Force
   ```

2. **Deploy to Azure**
   ```bash
   az webapp deployment source config-zip --name $appName --resource-group $resourceGroup --src deployment.zip
   ```

### Option C: Set up CI/CD with GitHub Actions

1. **Create GitHub Workflow**
   - Create `.github/workflows/deploy.yml` in your repository:

   ```yaml
   name: Deploy to Azure Web App

   on:
     push:
       branches: [ main ]
     workflow_dispatch:

   env:
     AZURE_WEBAPP_NAME: app-your-resource-base-name
     AZURE_WEBAPP_PACKAGE_PATH: '.'
     DOTNET_VERSION: '9.0'

   jobs:
     build-and-deploy:
       runs-on: windows-latest

       steps:
         - uses: actions/checkout@v4

         - name: Set up .NET Core
           uses: actions/setup-dotnet@v3
           with:
             dotnet-version: ${{ env.DOTNET_VERSION }}

         - name: Build with dotnet
           run: dotnet build --configuration Release

         - name: dotnet publish
           run: dotnet publish -c Release -o ${{env.DOTNET_ROOT}}/myapp

         - name: Deploy to Azure Web App
           uses: azure/webapps-deploy@v2
           with:
             app-name: ${{ env.AZURE_WEBAPP_NAME }}
             publish-profile: ${{ secrets.AZURE_WEBAPP_PUBLISH_PROFILE }}
             package: ${{env.DOTNET_ROOT}}/myapp
   ```

2. **Configure GitHub Secrets**
   - Go to your GitHub repository settings
   - Navigate to Secrets and variables > Actions
   - Download publish profile from Azure Portal (Web App > Get publish profile)
   - Add secret: `AZURE_WEBAPP_PUBLISH_PROFILE` with the publish profile content


## Step 6: Update Event Grid Configuration

### Update Event Grid Subscription

1. **Navigate to ACS Resource**
   - Go to your Azure Communication Services resource
   - Select "Events" under Monitoring

2. **Update Existing Subscription**
   - Find your existing Event Grid subscription for IncomingCall
   - Edit the subscription
   - Update the webhook endpoint URL to: `https://app-your-resource-base-name.azurewebsites.net/api/incomingCall`
   - Save the changes

3. **Using Azure CLI**
   ```bash
   # Get the subscription ID
   $subscriptionId = "your-subscription-id"
   $acsResourceName = "acs-your-resource-base-name"
   $eventSubscriptionName = "your-event-subscription-name"
   
   # Update the event subscription
   az eventgrid event-subscription update `
     --name $eventSubscriptionName `
     --source-resource-id "/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.Communication/CommunicationServices/$acsResourceName" `
     --endpoint "https://app-your-resource-base-name.azurewebsites.net/api/incomingCall"
   ```

---

## Monitoring and Health Checks

### Enable Application Insights

1. **Create Application Insights**
   ```bash
   $appInsightsName = "appi-your-resource-base-name"
   az monitor app-insights component create --app $appInsightsName --location $location --resource-group $resourceGroup --application-type web
   ```

2. **Connect to Web App**
   ```bash
   $instrumentationKey = az monitor app-insights component show --app $appInsightsName --resource-group $resourceGroup --query instrumentationKey --output tsv
   az webapp config appsettings set --name $appName --resource-group $resourceGroup --settings "APPLICATIONINSIGHTS_CONNECTION_STRING=InstrumentationKey=$instrumentationKey"
   ```

### Configure Health Check Endpoint

The application includes health checks that will be automatically available at:
- `https://app-your-resource-base-name.azurewebsites.net/health`

---

## Security Configuration

### Configure HTTPS Only

```bash
az webapp update --name $appName --resource-group $resourceGroup --https-only true
```

### Set up Custom Domain (Optional)

1. **Configure Custom Domain**
   - Purchase and configure a custom domain
   - Add CNAME record pointing to your Web App
   - Configure custom domain in Azure Portal
   - Enable SSL certificate

2. **Update Key Vault BaseUri**
   ```powershell
   Set-AzKeyVaultSecret -VaultName 'kv-your-resource-base-name' -Name 'BaseUri-Production' -SecretValue (ConvertTo-SecureString 'https://your-custom-domain.com' -AsPlainText -Force)
   ```

---

## Testing and Validation

### Test Application Deployment

1. **Verify Application is Running**
   - Navigate to `https://app-your-resource-base-name.azurewebsites.net/health`
   - Should return healthy status

2. **Check Key Vault Access**
   - Monitor application logs in Azure Portal
   - Verify no Key Vault access errors

3. **Test Telephony Integration**
   - Call your ACS phone number
   - Verify the call connects to your Copilot Studio agent
   - Monitor application logs for any issues

### Monitor Application Performance

1. **Application Insights Dashboard**
   - Monitor request metrics, dependencies, and exceptions
   - Set up alerts for critical issues

2. **Web App Metrics**
   - Monitor CPU, memory usage, and response times
   - Configure auto-scaling if needed

---

## Maintenance and Operations

### Ongoing Maintenance

### Backup and Recovery

1. **Enable Web App Backup**
   - Configure automated backups in Azure Portal
   - Set backup frequency and retention policy

2. **Key Vault Backup**
   - Key Vault automatically provides backup and recovery
   - Consider exporting secrets for disaster recovery planning

### Updates and Deployments

1. **Blue-Green Deployment**
   - Use deployment slots for zero-downtime deployments
   - Test in staging slot before swapping to production

2. **Monitoring and Alerting**
   - Set up alerts for application failures
   - Monitor Key Vault access and ACS call metrics

---

## Debugging Your Azure Web App

This section provides comprehensive debugging techniques for your deployed application.

### Real-Time Log Streaming

1. **Stream Live Logs using Azure CLI**
   ```bash
   # Stream application logs in real-time
   az webapp log tail --name $appName --resource-group $resourceGroup
   
   # Stream logs with specific log level
   az webapp log tail --name $appName --resource-group $resourceGroup --provider application
   ```

2. **Stream Logs from Azure Portal**
   - Navigate to your Web App in Azure Portal
   - Go to "Monitoring" > "Log stream"
   - Select "Application Logs" or "Web Server Logs"
   - View real-time logs as your application runs

### Application Insights Integration

1. **Enhanced Logging with Application Insights**
   ```bash
   # If not already created, create Application Insights
   $appInsightsName = "appi-your-resource-base-name"
   az monitor app-insights component create --app $appInsightsName --location $location --resource-group $resourceGroup --application-type web
   
   # Get the connection string
   $connectionString = az monitor app-insights component show --app $appInsightsName --resource-group $resourceGroup --query connectionString --output tsv
   
   # Set the connection string in your Web App
   az webapp config appsettings set --name $appName --resource-group $resourceGroup --settings "APPLICATIONINSIGHTS_CONNECTION_STRING=$connectionString"
   ```

2. **Query Application Insights**
   - Go to Azure Portal > Application Insights > your-app-insights
   - Use KQL (Kusto Query Language) to query logs:
   ```kql
   traces
   | where timestamp > ago(1h)
   | order by timestamp desc
   | take 100
   
   exceptions
   | where timestamp > ago(1h)
   | order by timestamp desc
   ```

### Remote Debugging Options

1. **Enable Detailed Error Messages**
   ```bash
   # Enable detailed error pages (temporarily for debugging)
   az webapp config appsettings set --name $appName --resource-group $resourceGroup --settings `
     "ASPNETCORE_DETAILEDERRORS=true" `
     "ASPNETCORE_LOGGING__LOGLEVEL__DEFAULT=Debug"
   ```

2. **Download Log Files**
   ```bash
   # Download all log files for offline analysis
   az webapp log download --name $appName --resource-group $resourceGroup --log-file logs.zip
   ```

### Debugging ACS and Key Vault Integration

1. **Test Key Vault Access**
   Add a test endpoint to your application for debugging Key Vault access:
   ```csharp
   // Add this to Program.cs for debugging (remove in production)
   app.MapGet("/debug/keyvault", async (IConfiguration config) =>
   {
       try
       {
           var acsConnectionString = config["AcsConnectionString"];
           var directLineSecret = config["DirectLineSecret"];
           var baseUri = config["BaseUri"];
           
           return Results.Ok(new
           {
               HasAcsConnectionString = !string.IsNullOrEmpty(acsConnectionString),
               HasDirectLineSecret = !string.IsNullOrEmpty(directLineSecret),
               HasBaseUri = !string.IsNullOrEmpty(baseUri),
               Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
               KeyVaultEndpoint = config["KeyVault:Endpoint"]
           });
       }
       catch (Exception ex)
       {
           return Results.Problem($"Error accessing configuration: {ex.Message}");
       }
   });
   ```

2. **Test ACS Webhook Endpoint**
   ```csharp
   // Add logging to your incoming call endpoint
   app.MapPost("/api/incomingCall", async (HttpRequest request) =>
   {
       var body = await new StreamReader(request.Body).ReadToEndAsync();
       app.Logger.LogInformation("Incoming call webhook received: {Body}", body);
       
       // Your existing logic here
   });
   ```

### Environment-Specific Debugging

1. **Set Debug-Friendly Configuration**
   ```bash
   # Temporarily enable verbose logging
   az webapp config appsettings set --name $appName --resource-group $resourceGroup --settings `
     "Logging__LogLevel__Microsoft.AspNetCore=Information" `
     "Logging__LogLevel__Azure=Information" `
     "Logging__LogLevel__System=Information"
   ```

2. **Enable Application Logging**
   ```bash
   # Enable application logging to file system
   az webapp log config --name $appName --resource-group $resourceGroup `
     --application-logging filesystem `
     --level information
   ```

### Debugging Call Automation Issues

1. **Monitor Call Events**
   Add comprehensive logging to your CallAutomationService:
   ```csharp
   // In CallAutomationService.cs, add detailed logging
   _logger.LogInformation("Call received from {CallerNumber} to {ReceiverNumber}", 
       incomingCall.From.PhoneNumber, incomingCall.To.PhoneNumber);
   
   _logger.LogInformation("Bot response received: {ResponseText}", responseText);
   
   _logger.LogError("Error in call processing: {Error}", ex.Message);
   ```

2. **Test Individual Components**
   Create test endpoints for each service:
   ```csharp
   // Test DirectLine connectivity
   app.MapGet("/debug/directline", async (HttpClient httpClient, IConfiguration config) =>
   {
       try
       {
           var secret = config["DirectLineSecret"];
           var response = await httpClient.PostAsync(
               "https://directline.botframework.com/v3/directline/conversations",
               new StringContent("{}", Encoding.UTF8, "application/json"));
           
           return Results.Ok(new { Status = response.StatusCode, HasSecret = !string.IsNullOrEmpty(secret) });
       }
       catch (Exception ex)
       {
           return Results.Problem(ex.Message);
       }
   });
   ```

### Performance Debugging

1. **Monitor Resource Usage**
   ```bash
   # Get CPU and memory metrics
   az monitor metrics list --resource "/subscriptions/your-subscription/resourceGroups/$resourceGroup/providers/Microsoft.Web/sites/$appName" `
     --metric "CpuPercentage,MemoryPercentage" `
     --start-time "2024-01-01T00:00:00Z" `
     --end-time "2024-01-01T23:59:59Z"
   ```

2. **Enable Profiling (temporarily)**
   ```bash
   # Enable Application Insights Profiler for performance analysis
   az webapp config appsettings set --name $appName --resource-group $resourceGroup --settings `
     "APPINSIGHTS_PROFILERFEATURE_VERSION=1.0.0" `
     "DiagnosticServices_EXTENSION_VERSION=~3"
   ```

### Debugging Checklist

When debugging issues, follow this systematic approach:

1. **✅ Check Application Health**
   - Visit `https://your-app.azurewebsites.net/health`
   - Verify all health checks pass

2. **✅ Verify Configuration**
   - Use `/debug/keyvault` endpoint to test configuration access
   - Check Application Settings in Azure Portal

3. **✅ Monitor Live Logs**
   - Stream logs during issue reproduction
   - Look for exceptions and error patterns

4. **✅ Test External Dependencies**
   - Key Vault access (managed identity)
   - DirectLine API connectivity
   - Azure Communication Services events

5. **✅ Analyze Call Flow**
   - Event Grid webhook delivery
   - Bot response processing
   - Audio synthesis and playback

### Security Considerations for Debugging

⚠️ **Important Security Notes:**
- Remove debug endpoints before production deployment
- Never log sensitive information (secrets, personal data)
- Disable detailed error messages in production
- Use Application Insights for production monitoring instead of debug endpoints

```bash
# Production-ready logging configuration
az webapp config appsettings set --name $appName --resource-group $resourceGroup --settings `
  "ASPNETCORE_DETAILEDERRORS=false" `
  "ASPNETCORE_LOGGING__LOGLEVEL__DEFAULT=Information"
```

---

## Troubleshooting

This section covers common issues and their solutions.

### Common Issues

1. **Key Vault Access Denied**
   - Verify managed identity is enabled
   - Check Key Vault RBAC role assignments or access policies
   - Ensure correct Key Vault endpoint URL

2. **Key Vault RBAC vs Access Policy Error**
   - If you get "Cannot set policies to a vault with '--enable-rbac-authorization' specified":
     - Your Key Vault uses RBAC (recommended)
     - Use `az role assignment create` instead of `az keyvault set-policy`
     - Assign "Key Vault Secrets User" role to the managed identity
   - To check if your Key Vault uses RBAC:
     ```bash
     az keyvault show --name $keyVaultName --resource-group $resourceGroup --query properties.enableRbacAuthorization
     ```

3. **Event Grid Subscription Not Working**
   - Verify webhook endpoint URL is correct
   - Check Event Grid subscription is active
   - Monitor Web App logs for incoming requests

4. **Application Not Starting**
   - Check application logs in Azure Portal
   - Verify all required configuration settings
   - Ensure .NET runtime version matches

### Log Analysis

```bash
# Stream logs from Web App
az webapp log tail --name $appName --resource-group $resourceGroup

# Download log files
az webapp log download --name $appName --resource-group $resourceGroup
```

---

## Cost Optimization

### Pricing Strategy

### Recommended Pricing Tiers

- **Development/Testing**: B1 Basic tier
- **Production**: S1 Standard tier or higher
- **High Availability**: P1V3 Premium tier with zone redundancy

### Cost Monitoring

- Set up Azure Cost Management alerts
- Monitor resource usage and optimize as needed
- Consider App Service Plan scaling based on usage patterns

---

## Conclusion

### Deployment Summary

Your ACSforMCS application is now deployed as an Azure Web App with:

✅ **Secure Key Vault integration** using managed identity  
✅ **Scalable hosting** on Azure App Service  
✅ **Continuous deployment capabilities** via multiple deployment options  
✅ **Comprehensive monitoring and logging** through Application Insights  
✅ **Production-ready security configurations** with HTTPS and RBAC  
✅ **Robust debugging capabilities** for troubleshooting issues  

### Next Steps

The telephony integration will continue to work seamlessly with your existing Azure Communication Services and Copilot Studio configuration, now hosted in a reliable cloud environment.

**Recommended Actions:**
1. **Monitor your application** through Azure Portal and Application Insights
2. **Set up alerts** for critical metrics and failures
3. **Configure automated backups** for business continuity
4. **Plan for scaling** based on call volume requirements
5. **Keep your deployment pipeline updated** for future enhancements

### Support and Maintenance

For ongoing support and maintenance:
- Review the [Debugging](#debugging-your-azure-web-app) section for troubleshooting techniques
- Monitor costs using the [Cost Optimization](#cost-optimization) guidelines
- Follow the [Maintenance and Operations](#maintenance-and-operations) best practices
- Consult the [Troubleshooting](#troubleshooting) section for common issues

---

**📞 Your telephony integration is now live and ready to serve callers through Azure Web App!**