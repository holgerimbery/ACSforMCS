<#
.SYNOPSIS
Validates ACSforMCS deployment readiness

.DESCRIPTION
This script validates that all prerequisites are met for deploying ACSforMCS,
including Azure resources, configuration, and permissions.

.PARAMETER KeyVaultName
Specify the Key Vault name to validate

.EXAMPLE
.\validate-deployment.ps1
.\validate-deployment.ps1 -KeyVaultName "my-keyvault"
#>

param(
    [string]$KeyVaultName
)

$ErrorActionPreference = "Stop"

Write-Host "ACS for MCS - Deployment Validation" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

$validationResults = @()

# Function to add validation result
function Add-ValidationResult {
    param($Test, $Status, $Message, $Action = "")
    $validationResults += [PSCustomObject]@{
        Test = $Test
        Status = $Status
        Message = $Message
        Action = $Action
    }
}

# Check Azure CLI
Write-Host "Checking Azure CLI..." -ForegroundColor Yellow
try {
    $azAccount = az account show --query "{name:name, id:id}" -o json | ConvertFrom-Json
    Add-ValidationResult "Azure CLI" "✅ PASS" "Authenticated as: $($azAccount.name)"
}
catch {
    Add-ValidationResult "Azure CLI" "❌ FAIL" "Not authenticated" "Run 'az login'"
}

# Get Key Vault name if not provided
if (-not $KeyVaultName) {
    try {
        $userSecrets = dotnet user-secrets list 2>$null
        if ($userSecrets) {
            $KeyVaultName = ($userSecrets | Where-Object { $_ -match "KeyVaultName" } | ForEach-Object { $_.Split("=")[1].Trim() })
        }
    }
    catch {
        # Ignore
    }
    
    if (-not $KeyVaultName) {
        $KeyVaultName = Read-Host "Enter Key Vault name for validation"
    }
}

if ($KeyVaultName) {
    # Check Key Vault access
    Write-Host "Checking Key Vault access..." -ForegroundColor Yellow
    try {
        $kvSecrets = az keyvault secret list --vault-name $KeyVaultName --query "[].name" -o tsv 2>$null
        Add-ValidationResult "Key Vault Access" "✅ PASS" "Can access Key Vault: $KeyVaultName"
        
        # Check required secrets
        $requiredSecrets = @(
            "AcsConnectionString",
            "DirectLineSecret", 
            "CognitiveServiceEndpoint",
            "AgentPhoneNumber",
            "BaseUri-Production"
        )
        
        Write-Host "Checking required secrets..." -ForegroundColor Yellow
        $missingSecrets = @()
        foreach ($secret in $requiredSecrets) {
            try {
                $value = az keyvault secret show --vault-name $KeyVaultName --name $secret --query "value" -o tsv 2>$null
                if ($value) {
                    Add-ValidationResult "Secret: $secret" "✅ PASS" "Secret exists and has value"
                } else {
                    Add-ValidationResult "Secret: $secret" "❌ FAIL" "Secret missing or empty" "Run setup-configuration.ps1"
                    $missingSecrets += $secret
                }
            }
            catch {
                Add-ValidationResult "Secret: $secret" "❌ FAIL" "Cannot access secret" "Check permissions and run setup-configuration.ps1"
                $missingSecrets += $secret
            }
        }
        
        # Check Web App if BaseUri is available
        try {
            $baseUri = az keyvault secret show --vault-name $KeyVaultName --name "BaseUri-Production" --query "value" -o tsv 2>$null
            if ($baseUri -and $baseUri -match "https://([^.]+)\.azurewebsites\.net") {
                $appName = $Matches[1]
                Write-Host "Checking Web App access..." -ForegroundColor Yellow
                
                $webApp = az webapp show --name $appName --query "{name:name, state:state, location:location}" -o json 2>$null | ConvertFrom-Json
                if ($webApp) {
                    Add-ValidationResult "Web App: $appName" "✅ PASS" "App exists and accessible (State: $($webApp.state), Location: $($webApp.location))"
                } else {
                    Add-ValidationResult "Web App: $appName" "❌ FAIL" "Cannot access web app" "Check app name and permissions"
                }
            }
        }
        catch {
            Add-ValidationResult "Web App Check" "⚠️ SKIP" "Cannot determine app name from BaseUri"
        }
        
    }
    catch {
        Add-ValidationResult "Key Vault Access" "❌ FAIL" "Cannot access Key Vault: $KeyVaultName" "Check vault name and permissions"
    }
}

# Check PowerShell version
Write-Host "Checking PowerShell version..." -ForegroundColor Yellow
if ($PSVersionTable.PSVersion.Major -ge 5) {
    Add-ValidationResult "PowerShell Version" "✅ PASS" "Version $($PSVersionTable.PSVersion) (5.1+ required)"
} else {
    Add-ValidationResult "PowerShell Version" "❌ FAIL" "Version $($PSVersionTable.PSVersion) is too old" "Update to PowerShell 5.1 or later"
}

# Check .NET (for user secrets)
Write-Host "Checking .NET availability..." -ForegroundColor Yellow
try {
    $dotnetVersion = dotnet --version 2>$null
    if ($dotnetVersion) {
        Add-ValidationResult ".NET CLI" "✅ PASS" "Version $dotnetVersion available"
    } else {
        Add-ValidationResult ".NET CLI" "⚠️ WARN" "Not available" "Install .NET SDK for user secrets support"
    }
}
catch {
    Add-ValidationResult ".NET CLI" "⚠️ WARN" "Not available" "Install .NET SDK for user secrets support"
}

# Display results
Write-Host ""
Write-Host "VALIDATION RESULTS:" -ForegroundColor Cyan
Write-Host "==================" -ForegroundColor Cyan
Write-Host ""

$passCount = 0
$failCount = 0
$warnCount = 0

foreach ($result in $validationResults) {
    $color = switch ($result.Status.Substring(0,1)) {
        "✅" { "Green"; $passCount++ }
        "❌" { "Red"; $failCount++ }
        "⚠️" { "Yellow"; $warnCount++ }
        default { "White" }
    }
    
    Write-Host "$($result.Status) $($result.Test): $($result.Message)" -ForegroundColor $color
    if ($result.Action) {
        Write-Host "    Action: $($result.Action)" -ForegroundColor Gray
    }
}

Write-Host ""
Write-Host "SUMMARY:" -ForegroundColor Cyan
Write-Host "✅ Passed: $passCount" -ForegroundColor Green
Write-Host "❌ Failed: $failCount" -ForegroundColor Red  
Write-Host "⚠️ Warnings: $warnCount" -ForegroundColor Yellow

Write-Host ""
if ($failCount -eq 0) {
    Write-Host "🎉 DEPLOYMENT READY!" -ForegroundColor Green
    Write-Host "All critical validations passed. You can proceed with deployment." -ForegroundColor Green
} else {
    Write-Host "🚫 NOT READY FOR DEPLOYMENT" -ForegroundColor Red
    Write-Host "Please resolve the failed validations before deploying." -ForegroundColor Red
    Write-Host ""
    Write-Host "Recommended next steps:" -ForegroundColor Yellow
    Write-Host "1. Run 'setup-configuration.ps1' to configure missing secrets" -ForegroundColor White
    Write-Host "2. Verify Azure resource access and permissions" -ForegroundColor White
    Write-Host "3. Re-run this validation script" -ForegroundColor White
}

exit $failCount