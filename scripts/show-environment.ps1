#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Displays the current environment configuration of the ACS for MCS application
.DESCRIPTION
    This script shows the current environment status by:
    - Checking the ASPNETCORE_ENVIRONMENT setting
    - Displaying key configuration values
    - Testing endpoint accessibility
    - Showing security configuration
    - Providing environment summary
.EXAMPLE
    .\show-environment.ps1
#>

# Script configuration
$ErrorActionPreference = "Stop"
$InformationPreference = "Continue"

Write-Information "ACS for MCS - Current Environment Status"
Write-Information ("=" * 60)

try {
    # Check if Azure CLI is installed and logged in
    Write-Information "INFO: Checking Azure CLI authentication..."
    $azAccount = az account show --output json 2>$null | ConvertFrom-Json
    if (!$azAccount) {
        throw "Please login to Azure CLI first: az login"
    }
    Write-Information "SUCCESS: Authenticated as: $($azAccount.user.name)"
    
    # Find project file
    $projectPath = Get-ChildItem -Path (Get-Location) -Filter "*.csproj" | Select-Object -First 1
    if (!$projectPath) {
        throw "No .csproj file found in current directory"
    }
    Write-Information "SUCCESS: Project found: $($projectPath.Name)"
    
    # Get Key Vault endpoint from user secrets
    $keyVaultEndpoint = dotnet user-secrets list --project $projectPath.FullName | Where-Object { $_ -match "KeyVault:Endpoint\s*=\s*(.+)" }
    if (!$keyVaultEndpoint) {
        throw "KeyVault:Endpoint not found in user secrets. Please run setup script first."
    }
    
    $keyVaultName = ($keyVaultEndpoint -split '=')[1].Trim() -replace 'https://([^.]+)\..*', '$1'
    Write-Information "INFO: Key Vault: $keyVaultName"
    
    # Detect app name from Key Vault name
    $appName = $keyVaultName -replace '^kv-', '' -replace '-.*$', ''
    if (!$appName) {
        throw "Could not detect app name from Key Vault name: $keyVaultName"
    }
    Write-Information "SUCCESS: Detected app name: $appName"
    
    # Find resource group
    Write-Information "INFO: Finding resource group for app: $appName"
    $resourceGroup = az webapp list --query "[?name=='$appName'].resourceGroup" --output tsv
    if (!$resourceGroup) {
        throw "Could not find resource group for app: $appName"
    }
    Write-Information "INFO: Resource group: $resourceGroup"
    
    # Get current environment
    Write-Information ""
    Write-Information "INFO: Retrieving current environment configuration..."
    $environmentSetting = az webapp config appsettings list --name $appName --resource-group $resourceGroup --query "[?name=='ASPNETCORE_ENVIRONMENT'].value" --output tsv
    
    if (!$environmentSetting) {
        Write-Warning "WARNING: ASPNETCORE_ENVIRONMENT not set - defaulting to Production"
        $environmentSetting = "Production"
    }
    
    Write-Information "SUCCESS: Current Environment: $environmentSetting"
    
    # Get app URL
    $appUrl = "https://$appName.azurewebsites.net"
    Write-Information "INFO: Application URL: $appUrl"
    
    # Get key configuration settings
    Write-Information ""
    Write-Information "INFO: Key Configuration Settings:"
    
    $healthCheckApiKey = az webapp config appsettings list --name $appName --resource-group $resourceGroup --query "[?name=='HealthCheckApiKey'].value" --output tsv
    if ($healthCheckApiKey) {
        Write-Information "  - Health Check API Key: Configured"
    } else {
        Write-Information "  - Health Check API Key: Not configured"
    }
    
    # Check web app configuration
    $webAppConfig = az webapp config show --name $appName --resource-group $resourceGroup --output json | ConvertFrom-Json
    Write-Information "  - Always On: $($webAppConfig.alwaysOn)"
    Write-Information "  - Web Sockets: $($webAppConfig.webSocketsEnabled)"
    Write-Information "  - HTTP/2: $($webAppConfig.http20Enabled)"
    Write-Information "  - HTTPS Only: $($webAppConfig.httpsOnly)"
    
    # Test endpoints based on environment
    Write-Information ""
    Write-Information "INFO: Testing endpoint accessibility..."
    
    # Test main application
    try {
        $mainResponse = Invoke-WebRequest -Uri $appUrl -Method Get -TimeoutSec 10 -ErrorAction SilentlyContinue
        if ($mainResponse.StatusCode -eq 200) {
            Write-Information "SUCCESS: Main application accessible (Status: $($mainResponse.StatusCode))"
        } else {
            Write-Information "INFO: Main application status: $($mainResponse.StatusCode)"
        }
    }
    catch {
        if ($_.Exception.Response.StatusCode -eq 503) {
            Write-Information "INFO: Main application starting up (503 - normal during startup)"
        } else {
            Write-Warning "WARNING: Could not test main application: $($_.Exception.Message)"
        }
    }
    
    # Test health endpoint
    try {
        if ($environmentSetting -eq "Development" -and $healthCheckApiKey) {
            $headers = @{"X-API-Key" = $healthCheckApiKey}
            $healthResponse = Invoke-WebRequest -Uri "$appUrl/health" -Method Get -Headers $headers -TimeoutSec 10 -ErrorAction SilentlyContinue
            if ($healthResponse.StatusCode -eq 200) {
                Write-Information "SUCCESS: Health endpoint accessible with API key (Development mode)"
            }
        } else {
            $healthResponse = Invoke-WebRequest -Uri "$appUrl/health" -Method Get -TimeoutSec 10 -ErrorAction SilentlyContinue
            Write-Information "WARNING: Health endpoint accessible without authentication (should be disabled in Production)"
        }
    }
    catch {
        if ($_.Exception.Response.StatusCode -eq 404) {
            if ($environmentSetting -eq "Production") {
                Write-Information "SUCCESS: Health endpoint properly disabled (Production mode)"
            } else {
                Write-Warning "WARNING: Health endpoint disabled (unexpected in Development mode)"
            }
        }
        elseif ($_.Exception.Response.StatusCode -eq 401 -or $_.Exception.Response.StatusCode -eq 403) {
            if ($environmentSetting -eq "Development") {
                Write-Information "SUCCESS: Health endpoint secured with API key (Development mode)"
            } else {
                Write-Information "INFO: Health endpoint secured (Production mode)"
            }
        }
        elseif ($_.Exception.Response.StatusCode -eq 503) {
            Write-Information "INFO: Health endpoint starting up (503 - normal during startup)"
        }
        else {
            Write-Information "INFO: Health endpoint status: $($_.Exception.Response.StatusCode)"
        }
    }
    
    # Test Swagger endpoint
    try {
        if ($environmentSetting -eq "Development" -and $healthCheckApiKey) {
            $headers = @{"X-API-Key" = $healthCheckApiKey}
            $swaggerResponse = Invoke-WebRequest -Uri "$appUrl/swagger" -Method Get -Headers $headers -TimeoutSec 10 -ErrorAction SilentlyContinue
            if ($swaggerResponse.StatusCode -eq 200) {
                Write-Information "SUCCESS: Swagger accessible with API key (Development mode)"
            }
        } else {
            $swaggerResponse = Invoke-WebRequest -Uri "$appUrl/swagger" -Method Get -TimeoutSec 10 -ErrorAction SilentlyContinue
            Write-Information "WARNING: Swagger accessible without authentication (should be disabled in Production)"
        }
    }
    catch {
        if ($_.Exception.Response.StatusCode -eq 404) {
            if ($environmentSetting -eq "Production") {
                Write-Information "SUCCESS: Swagger properly disabled (Production mode)"
            } else {
                Write-Warning "WARNING: Swagger disabled (unexpected in Development mode)"
            }
        }
        elseif ($_.Exception.Response.StatusCode -eq 401 -or $_.Exception.Response.StatusCode -eq 403) {
            if ($environmentSetting -eq "Development") {
                Write-Information "SUCCESS: Swagger secured with API key (Development mode)"
            } else {
                Write-Information "INFO: Swagger secured (Production mode)"
            }
        }
        elseif ($_.Exception.Response.StatusCode -eq 503) {
            Write-Information "INFO: Swagger starting up (503 - normal during startup)"
        }
        else {
            Write-Information "INFO: Swagger status: $($_.Exception.Response.StatusCode)"
        }
    }
    
    # Test webhook endpoint
    try {
        $webhookResponse = Invoke-WebRequest -Uri "$appUrl/api/incomingCall" -Method Post -Body '{}' -ContentType "application/json" -TimeoutSec 10
        if ($webhookResponse.StatusCode -eq 400) {
            Write-Information "SUCCESS: Webhook endpoint accessible (400 expected for empty payload)"
        }
    }
    catch {
        if ($_.Exception.Response.StatusCode -eq 400) {
            Write-Information "SUCCESS: Webhook endpoint accessible (400 expected for empty payload)"
        }
        elseif ($_.Exception.Response.StatusCode -eq 503) {
            Write-Information "INFO: Webhook endpoint starting up (503 - normal during startup)"
        }
        else {
            Write-Warning "WARNING: Could not test webhook endpoint: $($_.Exception.Message)"
        }
    }
    
    # Display environment summary
    Write-Information ""
    Write-Information "SUMMARY: Environment Configuration"
    Write-Information ("=" * 60)
    Write-Information "Environment: $environmentSetting"
    Write-Information "Application: $appUrl"
    Write-Information "Resource Group: $resourceGroup"
    Write-Information "Key Vault: $keyVaultName"
    
    if ($environmentSetting -eq "Development") {
        Write-Information ""
        Write-Information "DEVELOPMENT MODE FEATURES:"
        Write-Information "  - Swagger documentation available (with API key)"
        Write-Information "  - Health monitoring enabled (with API key)"
        Write-Information "  - Debug logging enabled"
        Write-Information "  - Enhanced error details"
        if ($healthCheckApiKey) {
            Write-Information ""
            Write-Information "API Key for testing: $healthCheckApiKey"
            Write-Information "  Health: curl -H 'X-API-Key: $healthCheckApiKey' $appUrl/health"
            Write-Information "  Swagger: $appUrl/swagger (add X-API-Key header)"
        }
    } else {
        Write-Information ""
        Write-Information "PRODUCTION MODE FEATURES:"
        Write-Information "  - Monitoring endpoints disabled for security"
        Write-Information "  - Swagger documentation disabled"
        Write-Information "  - Optimized performance settings"
        Write-Information "  - Enhanced security configuration"
    }
    
    Write-Information ""
    Write-Information "AVAILABLE ENDPOINTS:"
    Write-Information "  GET  / - Welcome message"
    Write-Information "  POST /api/incomingCall - ACS incoming call webhook"
    Write-Information "  POST /api/calls/{contextId} - ACS call events webhook"
    
    if ($environmentSetting -eq "Development") {
        Write-Information "  GET  /health - Health check (requires X-API-Key header)"
        Write-Information "  GET  /swagger - API documentation (requires X-API-Key header)"
    }
    
    Write-Information ""
    Write-Information "INFO: Use switch-to-development.ps1 or switch-to-production.ps1 to change environment"
    
}
catch {
    Write-Error "ERROR: Failed to retrieve environment status: $($_.Exception.Message)"
    exit 1
}