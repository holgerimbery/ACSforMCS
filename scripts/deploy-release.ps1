<#
.SYNOPSIS
Deploys ACSforMCS release package to Azure Web App

.DESCRIPTION
This script deploys a pre-built ACSforMCS release package to your Azure Web App.
It validates configuration, deploys the application, and verifies the deployment.

.PARAMETER Force
Skip confirmation prompts

.PARAMETER KeyVaultName
Specify the Key Vault name (optional, will prompt if not provided)

.PARAMETER AppName
Specify the target app name (optional, will be derived from BaseUri if not provided)

.EXAMPLE
.\deploy-release.ps1
.\deploy-release.ps1 -Force
.\deploy-release.ps1 -KeyVaultName "my-keyvault" -AppName "my-app"
#>

param(
    [switch]$Force,
    [string]$KeyVaultName,
    [string]$AppName
)

# Script configuration
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

Write-Host "ACS for MCS - Release Deployment" -ForegroundColor Cyan
Write-Host "===============================================" -ForegroundColor Cyan

# Check if running from correct directory
$packageFile = "ACSforMCS-*.zip"
$deploymentPackage = Get-ChildItem -Path . -Name $packageFile -ErrorAction SilentlyContinue

if (-not $deploymentPackage) {
    Write-Error "ERROR: No deployment package found. Please run this script from the deployment folder."
    exit 1
}

Write-Host "INFO: Found deployment package: $deploymentPackage" -ForegroundColor Green

# Check Azure CLI authentication
Write-Host "INFO: Checking Azure CLI authentication..."
try {
    $azAccount = az account show --query "{name:name, id:id}" -o json | ConvertFrom-Json
    Write-Host "SUCCESS: Authenticated as: $($azAccount.name)" -ForegroundColor Green
}
catch {
    Write-Error "ERROR: Not authenticated with Azure CLI. Please run 'az login' first."
    exit 1
}

# Get Key Vault name
if (-not $KeyVaultName) {
    # Try to get from user secrets first
    try {
        $userSecrets = dotnet user-secrets list --project .. 2>$null
        if ($userSecrets) {
            $KeyVaultName = ($userSecrets | Where-Object { $_ -match "KeyVaultName" } | ForEach-Object { $_.Split("=")[1].Trim() })
        }
    }
    catch {
        # User secrets not configured
    }
    
    if (-not $KeyVaultName) {
        $KeyVaultName = Read-Host "Enter your Key Vault name"
        if (-not $KeyVaultName) {
            Write-Error "ERROR: Key Vault name is required."
            exit 1
        }
    }
}

Write-Host "INFO: Using Key Vault: $KeyVaultName"

# Retrieve required configuration from Key Vault
Write-Host "INFO: Retrieving configuration from Key Vault..."
try {
    $secrets = @{
        "BaseUri-Production" = $(az keyvault secret show --vault-name $KeyVaultName --name "BaseUri-Production" --query "value" -o tsv)
        "AcsConnectionString" = $(az keyvault secret show --vault-name $KeyVaultName --name "AcsConnectionString" --query "value" -o tsv)
        "DirectLineSecret" = $(az keyvault secret show --vault-name $KeyVaultName --name "DirectLineSecret" --query "value" -o tsv)
        "CognitiveServiceEndpoint" = $(az keyvault secret show --vault-name $KeyVaultName --name "CognitiveServiceEndpoint" --query "value" -o tsv)
        "AgentPhoneNumber" = $(az keyvault secret show --vault-name $KeyVaultName --name "AgentPhoneNumber" --query "value" -o tsv)
    }
    
    $missingSecrets = $secrets.Keys | Where-Object { -not $secrets[$_] }
    if ($missingSecrets) {
        Write-Error "ERROR: Missing required secrets in Key Vault: $($missingSecrets -join ', ')"
        Write-Host "Please run setup-configuration.ps1 first to configure all required settings."
        exit 1
    }
    
    Write-Host "SUCCESS: All required secrets found in Key Vault" -ForegroundColor Green
}
catch {
    Write-Error "ERROR: Failed to retrieve configuration from Key Vault: $($_.Exception.Message)"
    exit 1
}

# Determine target application name
if (-not $AppName) {
    $appUrl = $secrets["BaseUri-Production"]
    if ($appUrl -match "https://([^.]+)\.azurewebsites\.net") {
        $AppName = $Matches[1]
    }
    else {
        Write-Error "ERROR: Cannot determine app name from BaseUri: $appUrl"
        exit 1
    }
}

Write-Host "INFO: Target application: $AppName"
Write-Host "INFO: Target URL: $($secrets['BaseUri-Production'])"

# Find resource group
Write-Host "INFO: Finding resource group for app: $AppName"
$resourceGroup = az webapp show --name $AppName --query "resourceGroup" -o tsv 2>$null
if (-not $resourceGroup) {
    Write-Error "ERROR: Could not find web app '$AppName' or access denied."
    exit 1
}

Write-Host "INFO: Resource group: $resourceGroup"

# Display deployment summary
Write-Host ""
Write-Host "DEPLOYMENT SUMMARY:" -ForegroundColor Yellow
Write-Host "  Package: $deploymentPackage" -ForegroundColor White
Write-Host "  Target: $AppName (Production environment)" -ForegroundColor White
Write-Host "  URL: $($secrets['BaseUri-Production'])" -ForegroundColor White
Write-Host "  Resource Group: $resourceGroup" -ForegroundColor White
Write-Host ""
Write-Warning "WARNING: This will deploy to Production environment!"
Write-Host ""

# Confirmation
if (-not $Force) {
    $confirmation = Read-Host "Continue with deployment? (y/N)"
    if ($confirmation -ne "y" -and $confirmation -ne "Y") {
        Write-Host "Deployment cancelled by user."
        exit 0
    }
}

# Deploy to Azure Web App
Write-Host ""
Write-Host "INFO: Deploying to Azure Web App..."
Write-Host "INFO: Target: $AppName in $resourceGroup"

try {
    az webapp deployment source config-zip --resource-group $resourceGroup --name $AppName --src $deploymentPackage
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "SUCCESS: Deployment completed successfully" -ForegroundColor Green
    }
    else {
        Write-Error "ERROR: Deployment failed with exit code $LASTEXITCODE"
        exit 1
    }
}
catch {
    Write-Error "ERROR: Deployment failed: $($_.Exception.Message)"
    exit 1
}

# Wait for application to start
Write-Host ""
Write-Host "INFO: Waiting for application startup..."
$maxRetries = 12
$retryCount = 0
$appReady = $false

do {
    $retryCount++
    Write-Host "INFO: Checking application status (attempt $retryCount/$maxRetries)..."
    
    try {
        $response = Invoke-WebRequest -Uri $secrets["BaseUri-Production"] -Method Get -TimeoutSec 10 -UseBasicParsing
        if ($response.StatusCode -eq 200) {
            $appReady = $true
            Write-Host "SUCCESS: Application is responding (Status: $($response.StatusCode))" -ForegroundColor Green
        }
    }
    catch {
        if ($retryCount -lt $maxRetries) {
            Write-Host "INFO: Application not ready yet, waiting..."
            Start-Sleep -Seconds 10
        }
    }
} while (-not $appReady -and $retryCount -lt $maxRetries)

if (-not $appReady) {
    Write-Warning "WARNING: Application may still be starting up. Check Azure portal for deployment status."
}

# Verify core endpoints
Write-Host ""
Write-Host "INFO: Verifying core endpoints..."

# Test webhook endpoint
try {
    $webhookUrl = "$($secrets['BaseUri-Production'])/api/incomingCall"
    $webhookResponse = Invoke-WebRequest -Uri $webhookUrl -Method Post -Body '{}' -ContentType "application/json" -TimeoutSec 15
    if ($webhookResponse.StatusCode -eq 400) {
        Write-Host "SUCCESS: Webhook endpoint accessible (400 expected for empty payload)" -ForegroundColor Green
    }
}
catch {
    if ($_.Exception.Response.StatusCode -eq 400) {
        Write-Host "SUCCESS: Webhook endpoint accessible (400 expected for empty payload)" -ForegroundColor Green
    } else {
        Write-Warning "WARNING: Could not verify webhook endpoint: $($_.Exception.Message)"
    }
}

# Final success message
Write-Host ""
Write-Host "SUCCESS: Deployment Complete!" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host "Environment: Production" -ForegroundColor White
Write-Host "Application: $($secrets['BaseUri-Production'])" -ForegroundColor White
Write-Host "Resource Group: $resourceGroup" -ForegroundColor White
Write-Host ""
Write-Host "AVAILABLE ENDPOINTS:" -ForegroundColor Yellow
Write-Host "  GET  $($secrets['BaseUri-Production'])/ - Welcome message" -ForegroundColor White
Write-Host "  POST $($secrets['BaseUri-Production'])/api/incomingCall - ACS incoming call webhook" -ForegroundColor White
Write-Host "  POST $($secrets['BaseUri-Production'])/api/calls/{contextId} - ACS call events webhook" -ForegroundColor White
Write-Host ""
Write-Host "READY FOR TESTING:" -ForegroundColor Yellow
Write-Host "  Agent Phone: $($secrets["AgentPhoneNumber"])" -ForegroundColor White
Write-Host "  Configure Event Grid to send events to: $($secrets['BaseUri-Production'])/api/incomingCall" -ForegroundColor White
Write-Host ""
Write-Host "INFO: Use ../scripts/show-environment.ps1 to verify deployment status" -ForegroundColor Cyan