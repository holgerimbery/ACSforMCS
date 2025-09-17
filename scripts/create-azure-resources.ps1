#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Creates all required Azure resources for ACS for MCS application
.DESCRIPTION
    This script automates the creation of all Azure resources needed for ACS for MCS:
    - Resource Group
    - Key Vault
    - Azure Communication Services
    - Cognitive Services (Speech)
    - App Service Plan
    - Web App
    
    The script validates name availability and follows the established naming conventions.
.PARAMETER ApplicationName
    The base name for your application (e.g., "myproject"). Must be globally unique for Azure Web Apps.
.PARAMETER Location
    Azure region for resource deployment (default: "West Europe")
.PARAMETER Environment
    Environment suffix for resources (default: "prod")
.PARAMETER ResourceGroupName
    Custom resource group name (optional, will generate from ApplicationName if not provided)
.PARAMETER SkuTier
    App Service Plan SKU tier (default: "B1" for Basic)
.PARAMETER Force
    Skip confirmation prompts
.PARAMETER ValidateOnly
    Only validate name availability without creating resources
.PARAMETER SkipNameValidation
    Skip name availability checks and proceed with creation (use with caution)
.EXAMPLE
    .\create-azure-resources.ps1 -ApplicationName "mycompany"
    .\create-azure-resources.ps1 -ApplicationName "myproject" -Location "East US" -Environment "dev"
    .\create-azure-resources.ps1 -ApplicationName "test" -ValidateOnly
    .\create-azure-resources.ps1 -ApplicationName "myproject" -SkipNameValidation
#>

param(
    [Parameter(Mandatory=$true)]
    [ValidateLength(3,15)]
    [ValidatePattern('^[a-z0-9]+$')]
    [string]$ApplicationName,
    
    [string]$Location = "West Europe",
    
    [ValidateSet("dev", "test", "prod")]
    [string]$Environment = "prod",
    
    [string]$ResourceGroupName = "",
    
    [ValidateSet("F1", "D1", "B1", "B2", "B3", "S1", "S2", "S3", "P1", "P2", "P3")]
    [string]$SkuTier = "B1",
    
    [switch]$Force,
    [switch]$ValidateOnly,
    [switch]$SkipNameValidation
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# Color functions for better output
function Write-Header { param([string]$Message) Write-Host $Message -ForegroundColor Cyan }
function Write-Success { param([string]$Message) Write-Host "SUCCESS: $Message" -ForegroundColor Green }
function Write-Warning { param([string]$Message) Write-Host "WARNING: $Message" -ForegroundColor Yellow }
function Write-Error { param([string]$Message) Write-Host "ERROR: $Message" -ForegroundColor Red }
function Write-Info { param([string]$Message) Write-Host "INFO: $Message" -ForegroundColor Blue }

# Function to convert location display name to Azure CLI name
function Get-AzureLocationName {
    param([string]$LocationDisplayName)
    
    # Common location mappings
    $locationMap = @{
        "West Europe" = "westeurope"
        "East US" = "eastus"
        "East US 2" = "eastus2"
        "West US 2" = "westus2"
        "Central US" = "centralus"
        "North Europe" = "northeurope"
        "South Central US" = "southcentralus"
        "West Central US" = "westcentralus"
        "UK South" = "uksouth"
        "Australia East" = "australiaeast"
        "Southeast Asia" = "southeastasia"
    }
    
    # If it's a known display name, return the CLI name
    if ($locationMap.ContainsKey($LocationDisplayName)) {
        return $locationMap[$LocationDisplayName]
    }
    
    # If it's already in CLI format (no spaces, lowercase), return as-is
    if ($LocationDisplayName -match '^[a-z]+$') {
        return $LocationDisplayName
    }
    
    # Fallback: remove spaces and convert to lowercase
    return $LocationDisplayName.Replace(" ", "").ToLower()
}

Write-Header "ACS for MCS - Azure Resource Creator"
Write-Header "======================================="

# Validate input
Write-Info "Validating input parameters..."
$ApplicationName = $ApplicationName.ToLower()

# Convert location to Azure CLI format
$AzureLocation = Get-AzureLocationName -LocationDisplayName $Location
Write-Info "Using Azure location: $AzureLocation"

# Generate resource names following the naming convention
if ([string]::IsNullOrEmpty($ResourceGroupName)) {
    $ResourceGroupName = "rg-$ApplicationName-$Environment"
}

$resourceNames = @{
    ResourceGroup = $ResourceGroupName
    KeyVault = "kv-$ApplicationName-$Environment"
    ACS = "acs-$ApplicationName-$Environment"
    CognitiveServices = "cog-speech-$ApplicationName-$Environment"
    AppServicePlan = "asp-$ApplicationName-$Environment"
    WebApp = "app-$ApplicationName-$Environment"
}

# Display resource names
Write-Header "Planned Resources:"
Write-Host "  Resource Group:       $($resourceNames.ResourceGroup)" -ForegroundColor White
Write-Host "  Key Vault:           $($resourceNames.KeyVault)" -ForegroundColor White
Write-Host "  Communication Svc:   $($resourceNames.ACS)" -ForegroundColor White
Write-Host "  Cognitive Services:  $($resourceNames.CognitiveServices)" -ForegroundColor White
Write-Host "  App Service Plan:    $($resourceNames.AppServicePlan)" -ForegroundColor White
Write-Host "  Web App:             $($resourceNames.WebApp)" -ForegroundColor White
Write-Host "  Location:            $Location ($AzureLocation)" -ForegroundColor White
Write-Host "  SKU:                 $SkuTier" -ForegroundColor White
Write-Host ""

# Check Azure CLI authentication
Write-Info "Checking Azure CLI authentication..."
try {
    $azAccount = az account show --query "{name:name, id:id, tenantId:tenantId}" --output json | ConvertFrom-Json
    Write-Success "Authenticated as: $($azAccount.name)"
    Write-Info "Subscription: $($azAccount.id)"
    Write-Info "Tenant: $($azAccount.tenantId)"
}
catch {
    Write-Error "Not authenticated with Azure CLI. Please run 'az login' first."
    exit 1
}

# Function to check name availability
function Test-ResourceNameAvailability {
    param([string]$ResourceType, [string]$Name)
    
    switch ($ResourceType) {
        "WebApp" {
            try {
                $available = az webapp list --query "[?name=='$Name']" --output json | ConvertFrom-Json
                return $available.Count -eq 0
            }
            catch {
                return $false
            }
        }
        "KeyVault" {
            try {
                # Use REST API to check Key Vault name availability (more reliable than az keyvault check-name)
                $subscriptionId = az account show --query "id" --output tsv
                $body = @{
                    name = $Name
                    type = "Microsoft.KeyVault/vaults"
                } | ConvertTo-Json
                
                # Create temporary file for the request body
                $tempFile = [System.IO.Path]::GetTempFileName()
                $body | Out-File -FilePath $tempFile -Encoding UTF8
                
                try {
                    $result = az rest --method POST --url "https://management.azure.com/subscriptions/$subscriptionId/providers/Microsoft.KeyVault/checkNameAvailability?api-version=2019-09-01" --body "@$tempFile" --headers "Content-Type=application/json" --output json | ConvertFrom-Json
                    return $result.nameAvailable -eq $true
                }
                finally {
                    # Clean up temp file
                    if (Test-Path $tempFile) {
                        Remove-Item $tempFile -Force
                    }
                }
            }
            catch {
                # If REST API fails, fall back to checking if vault exists in subscription
                Write-Warning "Key Vault availability check failed, falling back to subscription check..."
                try {
                    $existingVault = az keyvault show --name $Name --query "name" --output tsv 2>$null
                    return [string]::IsNullOrEmpty($existingVault)
                }
                catch {
                    # If both methods fail, assume name is not available to be safe
                    return $false
                }
            }
        }
        "CognitiveServices" {
            try {
                $result = az cognitiveservices account show --name $Name --resource-group "dummy" --query "name" --output tsv 2>$null
                return [string]::IsNullOrEmpty($result)
            }
            catch {
                return $true
            }
        }
        default {
            return $true
        }
    }
}

# Validate name availability
Write-Header "Checking Resource Name Availability"

if ($SkipNameValidation) {
    Write-Warning "Skipping name validation checks as requested"
    Write-Info "Proceeding with resource creation without validation"
} else {
    $availabilityResults = @()

    # Check Web App availability (most critical as it must be globally unique)
    Write-Info "Checking Web App name availability..."
    $webAppAvailable = Test-ResourceNameAvailability -ResourceType "WebApp" -Name $resourceNames.WebApp
    if ($webAppAvailable) {
        Write-Success "Web App name '$($resourceNames.WebApp)' is available"
        $availabilityResults += @{Resource="Web App"; Name=$resourceNames.WebApp; Available=$true}
    } else {
        Write-Error "Web App name '$($resourceNames.WebApp)' is already taken"
        $availabilityResults += @{Resource="Web App"; Name=$resourceNames.WebApp; Available=$false}
    }

    # Check Key Vault availability
    Write-Info "Checking Key Vault name availability..."
    $kvAvailable = Test-ResourceNameAvailability -ResourceType "KeyVault" -Name $resourceNames.KeyVault
    if ($kvAvailable) {
        Write-Success "Key Vault name '$($resourceNames.KeyVault)' is available"
        $availabilityResults += @{Resource="Key Vault"; Name=$resourceNames.KeyVault; Available=$true}
    } else {
        Write-Error "Key Vault name '$($resourceNames.KeyVault)' is not available"
        Write-Info "This could be due to: global name conflict, soft-deleted vault, or Azure service timeout"
        $availabilityResults += @{Resource="Key Vault"; Name=$resourceNames.KeyVault; Available=$false}
    }

    # Check for name conflicts
    $unavailableResources = $availabilityResults | Where-Object { -not $_.Available }
    if ($unavailableResources.Count -gt 0) {
        Write-Header "Name Availability Issues:"
        foreach ($resource in $unavailableResources) {
            Write-Error "$($resource.Resource): '$($resource.Name)' is not available"
        }
        Write-Host ""
        Write-Warning "Suggestions to resolve naming conflicts:"
        Write-Host "  1. Try a different ApplicationName (e.g., add your company name or random suffix)"
        Write-Host "  2. Use a different Environment suffix (dev, test, prod)"
        Write-Host "  3. For Key Vault: Check if it's in soft-deleted state and needs purging"
        Write-Host "  4. Check if resources are in a different subscription or region"
        Write-Host "  5. Use -SkipNameValidation if Azure services are experiencing issues"
        Write-Host ""
        Write-Host "Example alternatives:" -ForegroundColor Yellow
        Write-Host "  .\create-azure-resources.ps1 -ApplicationName '$ApplicationName-$(Get-Random -Maximum 999)'"
        Write-Host "  .\create-azure-resources.ps1 -ApplicationName '$ApplicationName' -Environment 'dev'"
        Write-Host "  .\create-azure-resources.ps1 -ApplicationName '$ApplicationName' -SkipNameValidation"
        Write-Host ""
        Write-Host "To check for soft-deleted Key Vaults:" -ForegroundColor Yellow
        Write-Host "  az keyvault list-deleted --query `"[?name=='$($resourceNames.KeyVault)']`""
        Write-Host ""
        exit 1
    }
}

# If ValidateOnly, stop here
if ($ValidateOnly) {
    Write-Success "All resource names are available!"
    Write-Info "Resources would be created with the names shown above."
    Write-Info "Run without -ValidateOnly to create the actual resources."
    exit 0
}

# Confirmation before creating resources
if (-not $Force) {
    Write-Header "Ready to Create Resources"
    Write-Warning "This will create the following Azure resources in your subscription:"
    Write-Host ""
    foreach ($kvp in $resourceNames.GetEnumerator()) {
        Write-Host "  $($kvp.Key): $($kvp.Value)" -ForegroundColor White
    }
    Write-Host ""
    
    $confirmation = Read-Host "Continue with resource creation? (y/N)"
    if ($confirmation -ne "y" -and $confirmation -ne "Y") {
        Write-Info "Resource creation cancelled by user."
        exit 0
    }
}

# Create resources
Write-Header "Creating Azure Resources"

try {
    # 1. Create Resource Group
    Write-Info "Creating Resource Group: $($resourceNames.ResourceGroup)"
    az group create --name $resourceNames.ResourceGroup --location $AzureLocation --output none
    if ($LASTEXITCODE -eq 0) {
        Write-Success "Resource Group created successfully"
    } else {
        throw "Failed to create Resource Group"
    }

    # 2. Create Key Vault
    Write-Info "Creating Key Vault: $($resourceNames.KeyVault)"
    az keyvault create `
        --name $resourceNames.KeyVault `
        --resource-group $resourceNames.ResourceGroup `
        --location $AzureLocation `
        --sku standard `
        --enable-rbac-authorization false `
        --output none
    if ($LASTEXITCODE -eq 0) {
        Write-Success "Key Vault created successfully"
    } else {
        throw "Failed to create Key Vault"
    }

    # 3. Create Azure Communication Services
    Write-Info "Creating Azure Communication Services: $($resourceNames.ACS)"
    az communication create `
        --name $resourceNames.ACS `
        --resource-group $resourceNames.ResourceGroup `
        --location "Global" `
        --data-location "Europe" `
        --output none
    if ($LASTEXITCODE -eq 0) {
        Write-Success "Azure Communication Services created successfully"
    } else {
        throw "Failed to create Azure Communication Services"
    }

    # 4. Create Cognitive Services (Speech)
    Write-Info "Creating Cognitive Services: $($resourceNames.CognitiveServices)"
    az cognitiveservices account create `
        --name $resourceNames.CognitiveServices `
        --resource-group $resourceNames.ResourceGroup `
        --kind "SpeechServices" `
        --sku "S0" `
        --location $AzureLocation `
        --yes `
        --output none
    if ($LASTEXITCODE -eq 0) {
        Write-Success "Cognitive Services created successfully"
    } else {
        throw "Failed to create Cognitive Services"
    }

    # 5. Create App Service Plan
    Write-Info "Creating App Service Plan: $($resourceNames.AppServicePlan)"
    az appservice plan create `
        --name $resourceNames.AppServicePlan `
        --resource-group $resourceNames.ResourceGroup `
        --location $AzureLocation `
        --sku $SkuTier `
        --output none
    if ($LASTEXITCODE -eq 0) {
        Write-Success "App Service Plan created successfully"
    } else {
        throw "Failed to create App Service Plan"
    }

    # 6. Create Web App
    Write-Info "Creating Web App: $($resourceNames.WebApp)"
    az webapp create `
        --name $resourceNames.WebApp `
        --resource-group $resourceNames.ResourceGroup `
        --plan $resourceNames.AppServicePlan `
        --runtime "dotnet:9" `
        --output none
    if ($LASTEXITCODE -eq 0) {
        Write-Success "Web App created successfully"
    } else {
        throw "Failed to create Web App"
    }

    # Configure Web App settings
    Write-Info "Configuring Web App settings..."
    az webapp config set `
        --name $resourceNames.WebApp `
        --resource-group $resourceNames.ResourceGroup `
        --always-on true `
        --output none

    # Set environment variable
    az webapp config appsettings set `
        --name $resourceNames.WebApp `
        --resource-group $resourceNames.ResourceGroup `
        --settings "ASPNETCORE_ENVIRONMENT=$Environment" `
        --output none

    Write-Success "Web App configuration completed"

} catch {
    Write-Error "Resource creation failed: $($_.Exception.Message)"
    Write-Warning "Some resources may have been created. Check the Azure portal and clean up if needed."
    exit 1
}

# Display success summary
Write-Header "Resource Creation Completed Successfully!"
Write-Host ""

# Collect and display resource information
Write-Header "Resource Information"

# Get ACS connection string
Write-Info "Retrieving Azure Communication Services connection string..."
$acsConnectionString = az communication list-key --name $resourceNames.ACS --resource-group $resourceNames.ResourceGroup --query "primaryConnectionString" --output tsv

# Get Cognitive Services endpoint
Write-Info "Retrieving Cognitive Services endpoint..."
$cognitiveEndpoint = az cognitiveservices account show --name $resourceNames.CognitiveServices --resource-group $resourceNames.ResourceGroup --query "properties.endpoint" --output tsv

# Display the information needed for configuration
Write-Header "Configuration Information"
Write-Host "Save this information for the setup-configuration.ps1 script:" -ForegroundColor Yellow
Write-Host ""
Write-Host "Key Vault Name:               $($resourceNames.KeyVault)" -ForegroundColor White
Write-Host "Key Vault Endpoint:           https://$($resourceNames.KeyVault).vault.azure.net/" -ForegroundColor White
Write-Host "Web App Name:                 $($resourceNames.WebApp)" -ForegroundColor White
Write-Host "Web App URL:                  https://$($resourceNames.WebApp).azurewebsites.net" -ForegroundColor White
Write-Host "ACS Connection String:        $acsConnectionString" -ForegroundColor White
Write-Host "Cognitive Services Endpoint:  $cognitiveEndpoint" -ForegroundColor White
Write-Host "Resource Group:               $($resourceNames.ResourceGroup)" -ForegroundColor White
Write-Host ""

# Display Azure CLI commands to add secrets to Key Vault
Write-Header "Next Steps: Configure Key Vault Secrets"
Write-Host "Run these commands to add the required secrets to your Key Vault:" -ForegroundColor Yellow
Write-Host ""

$secretCommands = @"
# 1. Add ACS Connection String
az keyvault secret set --vault-name "$($resourceNames.KeyVault)" --name "AcsConnectionString" --value "$acsConnectionString"

# 2. Add Base URIs
az keyvault secret set --vault-name "$($resourceNames.KeyVault)" --name "BaseUri-Development" --value "https://$($resourceNames.WebApp).azurewebsites.net"
az keyvault secret set --vault-name "$($resourceNames.KeyVault)" --name "BaseUri-Production" --value "https://$($resourceNames.WebApp).azurewebsites.net"

# 3. Add Cognitive Services Endpoint
az keyvault secret set --vault-name "$($resourceNames.KeyVault)" --name "CognitiveServiceEndpoint" --value "$cognitiveEndpoint"

# 4. Add remaining secrets (replace with your actual values)
az keyvault secret set --vault-name "$($resourceNames.KeyVault)" --name "DirectLineSecret" --value "YOUR_DIRECTLINE_SECRET"
az keyvault secret set --vault-name "$($resourceNames.KeyVault)" --name "AgentPhoneNumber" --value "YOUR_PHONE_NUMBER"
az keyvault secret set --vault-name "$($resourceNames.KeyVault)" --name "HealthCheckApiKey" --value "`$(([System.Guid]::NewGuid().ToString() -replace '-','').Substring(0,32))"
"@

Write-Host $secretCommands -ForegroundColor Cyan
Write-Host ""

# Save configuration to file
$configFile = "azure-resources-info.txt"
Write-Info "Saving configuration information to: $configFile"

$configContent = @"
ACS for MCS - Azure Resources Created $(Get-Date)
================================================

Application Name: $ApplicationName
Environment: $Environment
Location: $Location ($AzureLocation)
Resource Group: $($resourceNames.ResourceGroup)

Resources Created:
- Key Vault: $($resourceNames.KeyVault)
- Communication Services: $($resourceNames.ACS)
- Cognitive Services: $($resourceNames.CognitiveServices)
- App Service Plan: $($resourceNames.AppServicePlan)
- Web App: $($resourceNames.WebApp)

Configuration Values:
- Key Vault Endpoint: https://$($resourceNames.KeyVault).vault.azure.net/
- Web App URL: https://$($resourceNames.WebApp).azurewebsites.net
- ACS Connection String: $acsConnectionString
- Cognitive Services Endpoint: $cognitiveEndpoint

Next Steps:
1. Add remaining secrets to Key Vault (DirectLine, phone number, health check key)
2. Run setup-configuration.ps1 with -KeyVaultName "$($resourceNames.KeyVault)"
3. Deploy application using deploy-application.ps1
4. Configure Event Grid subscription for incoming calls

Key Vault Secret Commands:
$secretCommands
"@

$configContent | Out-File -FilePath $configFile -Encoding UTF8
Write-Success "Configuration saved to: $configFile"

Write-Header "Setup Complete!"
Write-Host ""
Write-Host "Your Azure resources are ready for ACS for MCS deployment." -ForegroundColor Green
Write-Host ""
Write-Host "Quick next steps:" -ForegroundColor Yellow
Write-Host "1. Add your Bot Framework DirectLine secret and phone number to Key Vault"
Write-Host "2. Run: .\setup-configuration.ps1 -KeyVaultName '$($resourceNames.KeyVault)'"
Write-Host "3. Run: .\deploy-application.ps1"
Write-Host ""
Write-Host "For detailed deployment instructions, see the Release Package Deployment Guide."