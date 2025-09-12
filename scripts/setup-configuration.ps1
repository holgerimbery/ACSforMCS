#Requires -Version 7.0

<#
.SYNOPSIS
    Initializes and validates local configuration required by all other scripts.

.DESCRIPTION
    This script sets up the local development environment by:
    - Auto-detecting or configuring Azure Key Vault endpoint
    - Initializing .NET user secrets for local development
    - Validating access to Key Vault and required secrets
    - Checking configuration readiness for deployment
    - Providing validation-only mode for troubleshooting

.PARAMETER KeyVaultName
    The name of the Azure Key Vault containing configuration secrets.
    If not provided, the script will attempt to auto-detect from existing user secrets.

.PARAMETER Force
    Force overwrite existing configuration without prompting.

.PARAMETER ValidateOnly
    Only validate existing configuration without making changes.

.EXAMPLE
    .\setup-configuration.ps1
    Auto-detect Key Vault and set up configuration interactively.

.EXAMPLE
    .\setup-configuration.ps1 -KeyVaultName "my-keyvault"
    Set up configuration with specific Key Vault name.

.EXAMPLE
    .\setup-configuration.ps1 -ValidateOnly
    Validate current configuration without making changes.

.EXAMPLE
    .\setup-configuration.ps1 -KeyVaultName "my-keyvault" -Force
    Force setup with specific Key Vault, overwriting existing configuration.

.NOTES
    Requirements:
    - Azure CLI must be installed and authenticated
    - .NET SDK must be installed for user secrets management
    - PowerShell 7.0 or later
    - Read permissions to Azure Key Vault
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$KeyVaultName,
    
    [Parameter(Mandatory = $false)]
    [switch]$Force,
    
    [Parameter(Mandatory = $false)]
    [switch]$ValidateOnly
)

# Enable strict mode and stop on errors
Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"
$InformationPreference = "Continue"

# Script configuration
$ProjectFile = "ACSforMCS.csproj"
$RequiredSecrets = @(
    "AcsConnectionString",
    "DirectLineSecret", 
    "CognitiveServiceEndpoint",
    "AgentPhoneNumber",
    "BaseUri-Development",
    "BaseUri-Production",
    "HealthCheckApiKey"
)

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
    
    # Check Azure CLI
    try {
        $azVersion = az version --output json 2>$null | ConvertFrom-Json
        Write-Success "Azure CLI version: $($azVersion.'azure-cli')"
    } catch {
        $issues += "Azure CLI not found. Install from: https://docs.microsoft.com/en-us/cli/azure/install-azure-cli"
    }
    
    # Check Azure CLI authentication
    try {
        $account = az account show --output json 2>$null | ConvertFrom-Json
        Write-Success "Azure account: $($account.user.name) (Subscription: $($account.name))"
    } catch {
        $issues += "Azure CLI not authenticated. Run 'az login' first."
    }
    
    # Check .NET SDK
    try {
        $dotnetVersion = dotnet --version
        Write-Success ".NET SDK version: $dotnetVersion"
    } catch {
        $issues += ".NET SDK not found. Install from: https://dotnet.microsoft.com/download"
    }
    
    # Check PowerShell version
    if ($PSVersionTable.PSVersion.Major -lt 7) {
        $issues += "PowerShell 7.0 or later required. Current version: $($PSVersionTable.PSVersion)"
    } else {
        Write-Success "PowerShell version: $($PSVersionTable.PSVersion)"
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

function Get-ExistingKeyVaultName {
    try {
        # Get user secrets in standard format (key = value)
        $secretsOutput = dotnet user-secrets list --project $ProjectFile 2>$null
        if ($secretsOutput) {
            # Look for KeyVault:Endpoint in the output
            $endpointLine = $secretsOutput | Where-Object { $_ -match "KeyVault:Endpoint\s*=\s*(.+)" }
            if ($endpointLine) {
                $endpoint = $matches[1].Trim()
                # Extract vault name from URL: https://vault-name.vault.azure.net/
                if ($endpoint -match "https://([^.]+)\.vault\.azure\.net/?") {
                    Write-Information "Found existing Key Vault configuration: $($matches[1])"
                    return $matches[1]
                }
            }
        }
    } catch {
        # Ignore errors when trying to read existing secrets
        Write-Information "No existing user secrets configuration found"
    }
    return $null
}

function Get-AvailableKeyVaults {
    try {
        $vaults = az keyvault list --query "[].name" --output json | ConvertFrom-Json
        return $vaults
    } catch {
        Write-Warning "Could not list available Key Vaults. Check your Azure permissions."
        return @()
    }
}

function Resolve-KeyVaultName {
    if ($ValidateOnly) {
        # In validation mode, try to get from existing user secrets first
        $existingVault = Get-ExistingKeyVaultName
        if ($existingVault) {
            Write-Information "Using existing Key Vault configuration: $existingVault"
            return $existingVault
        } else {
            throw "No existing Key Vault configuration found. Run without -ValidateOnly to set up configuration."
        }
    }
    
    if ($KeyVaultName) {
        Write-Information "Using specified Key Vault: $KeyVaultName"
        return $KeyVaultName
    }
    
    # Try to get from existing user secrets
    $existingVault = Get-ExistingKeyVaultName
    if ($existingVault) {
        Write-Information "Found existing Key Vault configuration: $existingVault"
        if (-not $Force) {
            $response = Read-Host "Use existing Key Vault '$existingVault'? (Y/n)"
            if ($response -eq "" -or $response.ToLower() -eq "y") {
                return $existingVault
            }
        } else {
            return $existingVault
        }
    }
    
    # Auto-detect available Key Vaults
    Write-Information "Auto-detecting available Key Vaults..."
    $availableVaults = Get-AvailableKeyVaults
    
    if ($availableVaults.Count -eq 0) {
        throw "No Key Vaults found. Ensure you have access to at least one Azure Key Vault."
    } elseif ($availableVaults.Count -eq 1) {
        $detectedVault = $availableVaults[0]
        Write-Information "Auto-detected Key Vault: $detectedVault"
        if (-not $Force) {
            $response = Read-Host "Use detected Key Vault '$detectedVault'? (Y/n)"
            if ($response -eq "" -or $response.ToLower() -eq "y") {
                return $detectedVault
            }
        } else {
            return $detectedVault
        }
    } else {
        Write-Information "Multiple Key Vaults available:"
        for ($i = 0; $i -lt $availableVaults.Count; $i++) {
            Write-Information "  $($i + 1). $($availableVaults[$i])"
        }
        
        do {
            $selection = Read-Host "Select Key Vault (1-$($availableVaults.Count))"
            $index = [int]$selection - 1
        } while ($index -lt 0 -or $index -ge $availableVaults.Count)
        
        return $availableVaults[$index]
    }
    
    throw "No Key Vault selected or detected."
}

function Test-KeyVaultAccess {
    param([string]$VaultName)
    
    try {
        az keyvault secret list --vault-name $VaultName --query "[0].name" --output tsv 2>$null | Out-Null
        Write-Success "Key Vault access confirmed: $VaultName"
        return $true
    } catch {
        Write-ErrorMessage "Cannot access Key Vault '$VaultName'. Check your permissions."
        return $false
    }
}

function Get-KeyVaultSecrets {
    param([string]$VaultName)
    
    Write-Information "Retrieving secrets from Key Vault: $VaultName"
    
    $secrets = @{}
    $missingSecrets = @()
    
    foreach ($secretName in $RequiredSecrets) {
        try {
            $secretValue = az keyvault secret show --vault-name $VaultName --name $secretName --query "value" --output tsv 2>$null
            if ($secretValue) {
                $secrets[$secretName] = $secretValue
                Write-Success "${secretName}: configured"
            } else {
                $missingSecrets += $secretName
                Write-Warning "${secretName}: not found"
            }
        } catch {
            $missingSecrets += $secretName
            Write-Warning "${secretName}: access denied or not found"
        }
    }
    
    return @{
        Secrets = $secrets
        Missing = $missingSecrets
    }
}

function Initialize-UserSecrets {
    param([string]$VaultName)
    
    if ($ValidateOnly) {
        Write-Information "Validation mode: Skipping user secrets initialization"
        return
    }
    
    Write-Header "INITIALIZING USER SECRETS"
    
    try {
        # Initialize user secrets if not already done
        dotnet user-secrets init --project $ProjectFile | Out-Null
        Write-Success "User secrets initialized"
        
        # Set Key Vault endpoint
        $keyVaultEndpoint = "https://$VaultName.vault.azure.net/"
        dotnet user-secrets set "KeyVault:Endpoint" $keyVaultEndpoint --project $ProjectFile | Out-Null
        Write-Success "Key Vault endpoint configured: $keyVaultEndpoint"
        
    } catch {
        throw "Failed to initialize user secrets: $($_.Exception.Message)"
    }
}

function Show-ValidationSummary {
    param(
        [string]$VaultName,
        [hashtable]$SecretResults,
        [bool]$IsValidationOnly = $false
    )
    
    Write-Header "CONFIGURATION VALIDATION"
    
    if ($IsValidationOnly) {
        # In validation mode, check if user secrets are configured
        try {
            $secretsOutput = dotnet user-secrets list --project $ProjectFile 2>$null
            $hasKeyVaultEndpoint = $secretsOutput | Where-Object { $_ -match "KeyVault:Endpoint" }
            if ($hasKeyVaultEndpoint) {
                Write-Success "KeyVault:Endpoint configured: $VaultName"
            } else {
                Write-Warning "KeyVault:Endpoint not configured in user secrets"
            }
        } catch {
            Write-Warning "KeyVault:Endpoint not configured in user secrets"
        }
    } else {
        Write-Success "KeyVault:Endpoint configured: $VaultName"
    }
    
    foreach ($secretName in $RequiredSecrets) {
        if ($SecretResults.Secrets.ContainsKey($secretName)) {
            Write-Success "${secretName}: configured"
        } else {
            Write-Warning "${secretName}: missing or inaccessible"
        }
    }
    
    if ($SecretResults.Missing.Count -eq 0) {
        Write-Information ""
        Write-Success "All configuration validation checks passed!"
        Write-Information "INFO: deploy-application.ps1 should work with current configuration"
    } else {
        Write-Information ""
        Write-Warning "Missing secrets found. You may need to:"
        Write-Information "  1. Ensure all required secrets exist in Key Vault '$VaultName'"
        Write-Information "  2. Check your Key Vault access permissions"
        Write-Information "  3. Verify secret names match the required list"
        
        Write-Information ""
        Write-Information "Missing secrets:"
        foreach ($missing in $SecretResults.Missing) {
            Write-Information "  - $missing"
        }
        
        if ($IsValidationOnly) {
            Write-Information ""
            Write-Information "To fix configuration issues, run:"
            Write-Information "  .\scripts\setup-configuration.ps1 -Force"
        }
    }
}

function Show-NextSteps {
    Write-Header "NEXT STEPS"
    
    Write-Information "Your local configuration is now set up. You can:"
    Write-Information ""
    Write-Information "1. Validate configuration:"
    Write-Information "   .\scripts\setup-configuration.ps1 -ValidateOnly"
    Write-Information ""
    Write-Information "2. Check environment status:"
    Write-Information "   .\scripts\show-environment.ps1"
    Write-Information ""
    Write-Information "3. Deploy application:"
    Write-Information "   .\scripts\deploy-application.ps1"
    Write-Information ""
    Write-Information "4. Switch environments:"
    Write-Information "   .\scripts\switch-to-development.ps1"
    Write-Information "   .\scripts\switch-to-production.ps1"
    Write-Information ""
    Write-Information "For help with any script:"
    Write-Information "   Get-Help .\scripts\<script-name>.ps1 -Full"
}

function Show-EnvironmentSecrets {
    if ($ValidateOnly) {
        return
    }
    
    Write-Header "ENVIRONMENT ACCESS COMMANDS"
    
    Write-Information "To retrieve specific secrets manually:"
    Write-Information ""
    foreach ($secretName in $RequiredSecrets) {
        Write-Information "  ${secretName}:"
        Write-Information "    az keyvault secret show --vault-name '$resolvedVaultName' --name '$secretName' --query 'value' --output tsv"
        Write-Information ""
    }
}

# Main execution
try {
    Write-Header "ACS FOR MCS - CONFIGURATION SETUP"
    
    if ($ValidateOnly) {
        Write-Information "Running in validation mode - no changes will be made"
    }
    
    # Check all prerequisites
    Test-Prerequisites
    
    # Resolve Key Vault name
    $resolvedVaultName = Resolve-KeyVaultName
    
    # Test Key Vault access
    if (-not (Test-KeyVaultAccess -VaultName $resolvedVaultName)) {
        throw "Cannot proceed without Key Vault access"
    }
    
    # Get and validate secrets
    $secretResults = Get-KeyVaultSecrets -VaultName $resolvedVaultName
    
    # Initialize user secrets (unless validation only)
    Initialize-UserSecrets -VaultName $resolvedVaultName
    
    # Show validation summary
    Show-ValidationSummary -VaultName $resolvedVaultName -SecretResults $secretResults -IsValidationOnly $ValidateOnly
    
    # Show environment access commands
    Show-EnvironmentSecrets
    
    # Show next steps (unless validation only)
    if (-not $ValidateOnly) {
        Show-NextSteps
    }
    
    Write-Information ""
    if ($secretResults.Missing.Count -eq 0) {
        Write-Success "Configuration setup completed successfully!"
    } else {
        Write-Warning "Configuration setup completed with warnings. Review missing secrets above."
    }
    
} catch {
    Write-Information ""
    Write-ErrorMessage "Configuration setup failed: $($_.Exception.Message)"
    Write-Information ""
    Write-Information "Troubleshooting tips:"
    Write-Information "  - Ensure you're running from the project root directory"
    Write-Information "  - Verify Azure CLI authentication: az account show"
    Write-Information "  - Check Key Vault permissions: Reader + Key Vault Secrets User roles"
    Write-Information "  - Test Key Vault access: az keyvault secret list --vault-name <vault-name>"
    Write-Information ""
    exit 1
}