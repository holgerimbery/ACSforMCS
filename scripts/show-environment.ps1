#Requires -Version 7.0

<#
.SYNOPSIS
    Displays the current environment configuration and status of the ACS for MCS application.

.DESCRIPTION
    This script provides comprehensive environment status information by:
    - Checking Azure CLI authentication and project configuration
    - Retrieving and displaying current environment settings
    - Testing endpoint accessibility and health status
    - Showing security configuration and available features
    - Providing detailed environment summary and usage instructions

.EXAMPLE
    .\show-environment.ps1
    Display complete environment status and configuration.

.NOTES
    Requirements:
    - Azure CLI must be installed and authenticated
    - .NET SDK must be installed for user secrets access
    - PowerShell 7.0 or later
    - Proper Key Vault configuration via setup-configuration.ps1
#>

[CmdletBinding()]
param()

# Enable strict mode and stop on errors
Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"
$InformationPreference = "Continue"

# Script configuration
$ProjectFile = "ACSforMCS.csproj"

function Write-Header {
    param([string]$Title)
    Write-Information ""
    Write-Information "========================================" 
    Write-Information $Title
    Write-Information "========================================" 
}

function Write-Success {
    param([string]$Message)
    Write-Host "✓ $Message" -ForegroundColor Green
}

function Write-Warning {
    param([string]$Message)
    Write-Host "⚠ $Message" -ForegroundColor Yellow
}

function Write-ErrorMessage {
    param([string]$Message)
    Write-Host "✗ $Message" -ForegroundColor Red
}

function Test-Prerequisites {
    Write-Header "CHECKING PREREQUISITES"
    
    $issues = @()
    
    # Check if we're in the right directory
    if (-not (Test-Path $ProjectFile)) {
        $issues += "Project file '$ProjectFile' not found. Run this script from the project root directory."
    } else {
        Write-Success "Project file found: $ProjectFile"
    }
    
    # Check Azure CLI authentication
    try {
        $account = az account show --output json 2>$null | ConvertFrom-Json
        Write-Success "Azure account: $($account.user.name) (Subscription: $($account.name))"
    } catch {
        $issues += "Azure CLI not authenticated. Run 'az login' first."
    }
    
    # Check user secrets configuration
    try {
        $secretsOutput = dotnet user-secrets list --project $ProjectFile 2>$null
        $hasKeyVaultEndpoint = $secretsOutput | Where-Object { $_ -match "KeyVault:Endpoint" }
        if ($hasKeyVaultEndpoint) {
            Write-Success "User secrets configured with Key Vault endpoint"
        } else {
            $issues += "Key Vault endpoint not configured. Run 'setup-configuration.ps1' first."
        }
    } catch {
        $issues += "Could not access user secrets. Ensure .NET SDK is installed."
    }
    
    if ($issues.Count -gt 0) {
        Write-Header "PREREQUISITE ISSUES FOUND"
        foreach ($issue in $issues) {
            Write-ErrorMessage $issue
        }
        throw "Prerequisites not met. Please resolve the issues above."
    }
    
    Write-Success "All prerequisites met!"
}

function Get-KeyVaultConfiguration {
    try {
        $secretsOutput = dotnet user-secrets list --project $ProjectFile 2>$null
        $endpointLine = $secretsOutput | Where-Object { $_ -match "KeyVault:Endpoint\s*=\s*(.+)" }
        if ($endpointLine) {
            $endpoint = $matches[1].Trim()
            if ($endpoint -match "https://([^.]+)\.vault\.azure\.net/?") {
                return $matches[1]
            }
        }
    } catch {
        throw "Could not retrieve Key Vault configuration from user secrets"
    }
    
    throw "Key Vault endpoint not found in user secrets. Run setup-configuration.ps1 first."
}

function Get-ApplicationInfo {
    param([string]$KeyVaultName)
    
    Write-Header "RETRIEVING APPLICATION INFORMATION"
    
    # Extract app name from Key Vault name (following naming convention)
    $appName = $KeyVaultName -replace '^kv-', '' -replace '-.*$', ''
    if (-not $appName) {
        throw "Could not detect app name from Key Vault name: $KeyVaultName"
    }
    Write-Success "Detected app name: $appName"
    
    # Find resource group
    try {
        $resourceGroup = az webapp list --query "[?name=='$appName'].resourceGroup" --output tsv 2>$null
        if (-not $resourceGroup) {
            throw "Could not find resource group for app: $appName"
        }
        Write-Success "Resource group: $resourceGroup"
    } catch {
        throw "Failed to retrieve resource group information. Check app name and Azure permissions."
    }
    
    # Get app URL
    $appUrl = "https://$appName.azurewebsites.net"
    Write-Success "Application URL: $appUrl"
    
    return @{
        AppName = $appName
        ResourceGroup = $resourceGroup
        AppUrl = $appUrl
    }
}

function Get-EnvironmentConfiguration {
    param(
        [string]$AppName,
        [string]$ResourceGroup,
        [string]$KeyVaultName
    )
    
    Write-Header "CURRENT ENVIRONMENT CONFIGURATION"
    
    # Get current environment setting
    try {
        $environmentSetting = az webapp config appsettings list --name $AppName --resource-group $ResourceGroup --query "[?name=='ASPNETCORE_ENVIRONMENT'].value" --output tsv 2>$null
        
        if (-not $environmentSetting) {
            Write-Warning "ASPNETCORE_ENVIRONMENT not set - defaulting to Production"
            $environmentSetting = "Production"
        } else {
            Write-Success "Environment: $environmentSetting"
        }
    } catch {
        Write-Warning "Could not retrieve environment setting - assuming Production"
        $environmentSetting = "Production"
    }
    
    # Get Health Check API Key if available
    $healthCheckApiKey = $null
    try {
        $healthCheckApiKey = az keyvault secret show --vault-name $KeyVaultName --name "HealthCheckApiKey" --query "value" --output tsv 2>$null
        if ($healthCheckApiKey) {
            Write-Success "Health Check API Key: Available in Key Vault"
        } else {
            Write-Warning "Health Check API Key: Not found in Key Vault"
        }
    } catch {
        Write-Warning "Health Check API Key: Could not access Key Vault secret"
    }
    
    return @{
        Environment = $environmentSetting
        HasHealthCheckApiKey = ($null -ne $healthCheckApiKey)
    }
}

function Test-ApplicationEndpoints {
    param(
        [string]$AppUrl,
        [string]$Environment,
        [bool]$HasHealthCheckApiKey
    )
    
    Write-Header "TESTING APPLICATION ENDPOINTS"
    
    # Test main endpoint
    try {
        $response = Invoke-WebRequest -Uri $AppUrl -Method GET -TimeoutSec 30 -UseBasicParsing
        if ($response.StatusCode -eq 200) {
            Write-Success "Main endpoint ($AppUrl): Accessible"
        } else {
            Write-Warning "Main endpoint returned status code: $($response.StatusCode)"
        }
    } catch {
        Write-Warning "Main endpoint: $($_.Exception.Message)"
    }
    
    # Test ACS webhook endpoint
    try {
        $webhookUrl = "$AppUrl/api/incomingCall"
        $response = Invoke-WebRequest -Uri $webhookUrl -Method GET -TimeoutSec 10 -UseBasicParsing
        # Expect 405 Method Not Allowed for GET on POST endpoint
        if ($response.StatusCode -eq 405) {
            Write-Success "ACS webhook endpoint: Available (expects POST requests)"
        } else {
            Write-Warning "ACS webhook returned unexpected status: $($response.StatusCode)"
        }
    } catch {
        if ($_.Exception.Response.StatusCode -eq 405) {
            Write-Success "ACS webhook endpoint: Available (expects POST requests)"
        } elseif ($_.Exception.Response.StatusCode -eq 503) {
            Write-Warning "ACS webhook endpoint: Service starting up (503)"
        } else {
            Write-Warning "ACS webhook endpoint: $($_.Exception.Message)"
        }
    }
    
    # Test development endpoints if in development mode
    if ($Environment -eq "Development" -and $HasHealthCheckApiKey) {
        try {
            $healthUrl = "$AppUrl/health"
            $response = Invoke-WebRequest -Uri $healthUrl -Method GET -TimeoutSec 10 -UseBasicParsing
            # Expect 401 Unauthorized without API key
            if ($response.StatusCode -eq 401) {
                Write-Success "Health endpoint: Protected (requires API key)"
            } else {
                Write-Warning "Health endpoint returned unexpected status: $($response.StatusCode)"
            }
        } catch {
            if ($_.Exception.Response.StatusCode -eq 401) {
                Write-Success "Health endpoint: Protected (requires API key)"
            } else {
                Write-Warning "Health endpoint: $($_.Exception.Message)"
            }
        }
        
        try {
            $swaggerUrl = "$AppUrl/swagger"
            $response = Invoke-WebRequest -Uri $swaggerUrl -Method GET -TimeoutSec 10 -UseBasicParsing
            # Expect 401 Unauthorized without API key
            if ($response.StatusCode -eq 401) {
                Write-Success "Swagger documentation: Protected (requires API key)"
            } else {
                Write-Warning "Swagger endpoint returned unexpected status: $($response.StatusCode)"
            }
        } catch {
            if ($_.Exception.Response.StatusCode -eq 401) {
                Write-Success "Swagger documentation: Protected (requires API key)"
            } else {
                Write-Warning "Swagger endpoint: $($_.Exception.Message)"
            }
        }
    }
}

function Show-EnvironmentSummary {
    param(
        [string]$Environment,
        [string]$AppUrl,
        [string]$ResourceGroup,
        [string]$KeyVaultName,
        [bool]$HasHealthCheckApiKey
    )
    
    Write-Header "ENVIRONMENT SUMMARY"
    
    Write-Information "Environment: $Environment"
    Write-Information "Application: $AppUrl"
    Write-Information "Resource Group: $ResourceGroup"
    Write-Information "Key Vault: $KeyVaultName"
    Write-Information ""
    
    if ($Environment -eq "Development") {
        Write-Information "DEVELOPMENT MODE FEATURES:"
        Write-Information "   ✅ Swagger documentation available (API key protected)"
        Write-Information "   ✅ Health monitoring enabled (API key protected)"
        Write-Information "   ✅ Debug logging enabled"
        Write-Information "   ✅ Enhanced error details"
        Write-Information ""
        
        if ($HasHealthCheckApiKey) {
            Write-Information "API KEY ACCESS:"
            Write-Information "   API Key: Available in Key Vault (run setup-configuration.ps1 for access commands)"
            Write-Information "   Health: curl -H 'X-API-Key: [your-api-key]' $AppUrl/health"
            Write-Information "   Swagger: $AppUrl/swagger (add X-API-Key header)"
        } else {
            Write-Warning "   Health Check API Key not available"
        }
    } else {
        Write-Information "PRODUCTION MODE FEATURES:"
        Write-Information "   Monitoring endpoints disabled for security"
        Write-Information "   Swagger documentation disabled"
        Write-Information "   Optimized performance settings"
        Write-Information "   Enhanced security configuration"
    }
    
    Write-Information ""
    Write-Information "AVAILABLE ENDPOINTS:"
    Write-Information "   GET  /                        - Welcome message (public)"
    Write-Information "   POST /api/incomingCall        - ACS incoming call webhook (public)"
    Write-Information "   POST /api/calls/{contextId}   - ACS call events webhook (public)"
    
    if ($Environment -eq "Development") {
        Write-Information "   GET  /health                  - Health check (API key required)"
        Write-Information "   GET  /health/calls            - Call monitoring (API key required)"
        Write-Information "   GET  /health/config           - Configuration status (API key required)"
        Write-Information "   GET  /health/metrics          - System metrics (API key required)"
        Write-Information "   GET  /swagger                 - API documentation (API key required)"
    }
}

function Show-NextSteps {
    param([string]$Environment)
    
    Write-Header "NEXT STEPS"
    
    Write-Information "Available actions:"
    Write-Information ""
    
    if ($Environment -eq "Development") {
        Write-Information "Switch to Production:"
        Write-Information "   .\scripts\switch-to-production.ps1"
        Write-Information ""
    } else {
        Write-Information "Switch to Development:"
        Write-Information "   .\scripts\switch-to-development.ps1"
        Write-Information ""
    }
    
    Write-Information "Deploy Application:"
    Write-Information "   .\scripts\deploy-application.ps1"
    Write-Information ""
    Write-Information "Reconfigure Setup:"
    Write-Information "   .\scripts\setup-configuration.ps1 -Force"
    Write-Information ""
    Write-Information "Validate Configuration:"
    Write-Information "   .\scripts\setup-configuration.ps1 -ValidateOnly"
    Write-Information ""
    Write-Information "Get Help:"
    Write-Information "   Get-Help .\scripts\show-environment.ps1 -Full"
}

# Main execution
try {
    Write-Header "ACS FOR MCS - ENVIRONMENT STATUS"
    
    # Check all prerequisites
    Test-Prerequisites
    
    # Get Key Vault configuration
    $keyVaultName = Get-KeyVaultConfiguration
    Write-Success "Key Vault configured: $keyVaultName"
    
    # Get application information
    $appInfo = Get-ApplicationInfo -KeyVaultName $keyVaultName
    
    # Get environment configuration
    $envConfig = Get-EnvironmentConfiguration -AppName $appInfo.AppName -ResourceGroup $appInfo.ResourceGroup -KeyVaultName $keyVaultName
    
    # Test application endpoints
    Test-ApplicationEndpoints -AppUrl $appInfo.AppUrl -Environment $envConfig.Environment -HasHealthCheckApiKey $envConfig.HasHealthCheckApiKey
    
    # Show environment summary
    Show-EnvironmentSummary -Environment $envConfig.Environment -AppUrl $appInfo.AppUrl -ResourceGroup $appInfo.ResourceGroup -KeyVaultName $keyVaultName -HasHealthCheckApiKey $envConfig.HasHealthCheckApiKey
    
    # Show next steps
    Show-NextSteps -Environment $envConfig.Environment
    
    Write-Information ""
    Write-Success "Environment status check completed successfully!"
    
} catch {
    Write-Information ""
    Write-ErrorMessage "Environment status check failed: $($_.Exception.Message)"
    Write-Information ""
    Write-Information "Troubleshooting tips:"
    Write-Information "  - Ensure you're running from the project root directory"
    Write-Information "  - Verify Azure CLI authentication: az account show"
    Write-Information "  - Run configuration setup: .\scripts\setup-configuration.ps1"
    Write-Information "  - Check Key Vault permissions: Reader + Key Vault Secrets User roles"
    Write-Information ""
    exit 1
}