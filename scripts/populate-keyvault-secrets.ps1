#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Populates Azure Key Vault with required secrets for ACS for MCS application
.DESCRIPTION
    This script helps populate an Azure Key Vault with all the required secrets for the ACS for MCS application.
    It handles Windows PowerShell compatibility for generating secure API keys.
.PARAMETER KeyVaultName
    The name of the Azure Key Vault to populate
.PARAMETER DirectLineSecret
    Bot Framework DirectLine secret (optional, will prompt if not provided)
.PARAMETER AgentPhoneNumber
    Phone number for agent transfers (optional, will prompt if not provided)
.PARAMETER Force
    Skip confirmation prompts
.EXAMPLE
    .\populate-keyvault-secrets.ps1 -KeyVaultName "kv-myapp-prod"
    .\populate-keyvault-secrets.ps1 -KeyVaultName "kv-myapp-prod" -DirectLineSecret "your-secret" -AgentPhoneNumber "+1234567890"
#>

param(
    [Parameter(Mandatory=$true)]
    [string]$KeyVaultName,
    
    [string]$DirectLineSecret = "",
    
    [string]$AgentPhoneNumber = "",
    
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# Color functions for better output
function Write-Header { param([string]$Message) Write-Host $Message -ForegroundColor Cyan }
function Write-Success { param([string]$Message) Write-Host "SUCCESS: $Message" -ForegroundColor Green }
function Write-Warning { param([string]$Message) Write-Host "WARNING: $Message" -ForegroundColor Yellow }
function Write-Error { param([string]$Message) Write-Host "ERROR: $Message" -ForegroundColor Red }
function Write-Info { param([string]$Message) Write-Host "INFO: $Message" -ForegroundColor Blue }

Write-Header "ACS for MCS - Key Vault Secret Populator"
Write-Header "=========================================="

# Check Azure CLI authentication
Write-Info "Checking Azure CLI authentication..."
try {
    $azAccount = az account show --query "{name:name, id:id}" --output json | ConvertFrom-Json
    Write-Success "Authenticated as: $($azAccount.name)"
}
catch {
    Write-Error "Not authenticated with Azure CLI. Please run 'az login' first."
    exit 1
}

# Verify Key Vault exists and is accessible
Write-Info "Verifying Key Vault access..."
try {
    $vault = az keyvault show --name $KeyVaultName --query "name" --output tsv
    Write-Success "Key Vault '$KeyVaultName' is accessible"
}
catch {
    Write-Error "Cannot access Key Vault '$KeyVaultName'. Please check the name and your permissions."
    exit 1
}

# Generate Health Check API Key
Write-Info "Generating Health Check API Key..."
$healthCheckKey = ([System.Guid]::NewGuid().ToString() -replace '-','').Substring(0,32)
Write-Success "Generated 32-character API key"

# Get DirectLine Secret if not provided
if ([string]::IsNullOrEmpty($DirectLineSecret)) {
    Write-Info "DirectLine Secret is required for Bot Framework integration."
    Write-Info "You can get this from Azure Portal > Bot Service > Channels > DirectLine"
    $DirectLineSecret = Read-Host "Enter your DirectLine Secret"
    if ([string]::IsNullOrEmpty($DirectLineSecret)) {
        Write-Error "DirectLine Secret is required."
        exit 1
    }
}

# Get Agent Phone Number if not provided
if ([string]::IsNullOrEmpty($AgentPhoneNumber)) {
    Write-Info "Agent Phone Number is required for call transfers."
    Write-Info "Use international format, e.g., +1234567890"
    $AgentPhoneNumber = Read-Host "Enter Agent Phone Number"
    if ([string]::IsNullOrEmpty($AgentPhoneNumber)) {
        Write-Error "Agent Phone Number is required."
        exit 1
    }
}

# Display what will be added
Write-Header "Secrets to be added to Key Vault:"
Write-Host "  DirectLineSecret: $(if ($DirectLineSecret.Length -gt 10) { $DirectLineSecret.Substring(0,10) + '...' } else { $DirectLineSecret })" -ForegroundColor White
Write-Host "  AgentPhoneNumber: $AgentPhoneNumber" -ForegroundColor White
Write-Host "  HealthCheckApiKey: $($healthCheckKey.Substring(0,8))..." -ForegroundColor White
Write-Host ""

# Confirmation
if (-not $Force) {
    $confirmation = Read-Host "Add these secrets to Key Vault '$KeyVaultName'? (y/N)"
    if ($confirmation -ne "y" -and $confirmation -ne "Y") {
        Write-Info "Operation cancelled by user."
        exit 0
    }
}

# Add secrets to Key Vault
Write-Header "Adding secrets to Key Vault..."

try {
    # Add DirectLine Secret
    Write-Info "Adding DirectLine Secret..."
    az keyvault secret set --vault-name $KeyVaultName --name "DirectLineSecret" --value $DirectLineSecret --output none
    Write-Success "DirectLine Secret added"

    # Add Agent Phone Number
    Write-Info "Adding Agent Phone Number..."
    az keyvault secret set --vault-name $KeyVaultName --name "AgentPhoneNumber" --value $AgentPhoneNumber --output none
    Write-Success "Agent Phone Number added"

    # Add Health Check API Key
    Write-Info "Adding Health Check API Key..."
    az keyvault secret set --vault-name $KeyVaultName --name "HealthCheckApiKey" --value $healthCheckKey --output none
    Write-Success "Health Check API Key added"

    Write-Header "All secrets added successfully!"
    Write-Host ""
    Write-Success "Key Vault '$KeyVaultName' is now fully configured."
    Write-Host ""
    Write-Info "Next steps:"
    Write-Host "1. Run: .\setup-configuration.ps1 -KeyVaultName '$KeyVaultName'"
    Write-Host "2. Run: .\deploy-application.ps1"
}
catch {
    Write-Error "Failed to add secrets to Key Vault: $($_.Exception.Message)"
    exit 1
}