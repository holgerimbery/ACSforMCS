#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Switches the ACS for MCS application to Development environment
.DESCRIP    # Initialize user secrets if not already done
    Write-Information "INFO: Initializing .NET user secrets..."
    try {
        dotnet user-secrets init --project $projectPath.FullName 2>$null
    }
    catch {
        # Already initialized, continue
    }
    
    # Get or set Key Vault endpoint
    Write-Information "INFO: Configuring Key Vault endpoint..."
    $keyVaultEndpoint = Get-OrSet-UserSecret -Key "KeyVault:Endpoint" -ProjectPath $projectPath.FullName
    
    # Extract Key Vault name from endpoint
    $keyVaultName = $keyVaultEndpoint -replace "https://", "" -replace "\.vault\.azure\.net/?$", ""
    if ([string]::IsNullOrWhiteSpace($keyVaultName)) {
        throw "Invalid Key Vault endpoint format. Expected: https://your-keyvault.vault.azure.net/"
    }
    Write-Information "INFO: Key Vault name: $keyVaultName"pt automatically configures the Azure Web App for Development environment by:
    - Retrieving configuration from Azure Key Vault
    - Setting up .NET user secrets for local development
    - Switching the web app environment to Development
    - Enabling monitoring endpoints and Swagger for debugging
.PARAMETER Force
    Skip confirmation prompts
.EXAMPLE
    .\switch-to-development.ps1
    .\switch-to-development.ps1 -Force
#>

param(
    [switch]$Force
)

# Script configuration
$ErrorActionPreference = "Stop"
$InformationPreference = "Continue"

Write-Information "ACS for MCS - Switching to Development Environment"
Write-Information ("=" * 60)

# Function to get or set .NET user secret
function Get-OrSet-UserSecret {
    param(
        [string]$Key,
        [string]$ProjectPath
    )
    
    try {
        $existingValue = dotnet user-secrets list --project $ProjectPath | Where-Object { $_ -match "$Key\s*=\s*(.+)" }
        if ($existingValue) {
            $value = ($existingValue -split '=', 2)[1].Trim()
            Write-Information "SUCCESS: Found existing user secret for ${Key}"
            return $value
        }
    }
    catch {
        Write-Warning "Could not retrieve existing user secret for $Key"
    }
    
    Write-Information "PROMPT: Please enter the value for ${Key}:"
    $value = Read-Host -Prompt $Key
    
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Value for ${Key} cannot be empty"
    }
    
    try {
        dotnet user-secrets set $Key $value --project $ProjectPath
        Write-Information "SUCCESS: Set user secret for ${Key}"
        return $value
    }
    catch {
        throw "Failed to set user secret for ${Key}: $($_.Exception.Message)"
    }
}

# Function to get Key Vault secrets
function Get-KeyVaultSecrets {
    param([string]$KeyVaultName)
    
    Write-Information "INFO: Retrieving secrets from Key Vault: $KeyVaultName"
    
    try {
        $secretNames = az keyvault secret list --vault-name $KeyVaultName --query "[].name" --output tsv
        if (!$secretNames) {
            throw "No secrets found in Key Vault or access denied"
        }
        
        $secrets = @{}
        foreach ($secretName in $secretNames) {
            try {
                $secretValue = az keyvault secret show --vault-name $KeyVaultName --name $secretName --query "value" --output tsv
                $secrets[$secretName] = $secretValue
                Write-Information "  SUCCESS: Retrieved: $secretName"
            }
            catch {
                Write-Warning "  WARNING: Could not retrieve: $secretName - $($_.Exception.Message)"
            }
        }
        
        return $secrets
    }
    catch {
        throw "Failed to retrieve secrets from Key Vault: $_"
    }
}

try {
    # Check if Azure CLI is installed and logged in
    Write-Information "INFO: Checking Azure CLI authentication..."
    $azAccount = az account show --output json 2>$null | ConvertFrom-Json
    if (!$azAccount) {
        Write-Information "INFO: Please login to Azure CLI..."
        az login
        $azAccount = az account show --output json | ConvertFrom-Json
    }
    Write-Information "SUCCESS: Authenticated as: $($azAccount.user.name)"
    
    # Find project file
    $projectPath = Get-ChildItem -Path (Get-Location) -Filter "*.csproj" | Select-Object -First 1
    if (!$projectPath) {
        throw "No .csproj file found in current directory"
    }
    Write-Information "SUCCESS: Project found: $($projectPath.Name)"
    
    # Initialize user secrets if not already done
    Write-Information "INFO: Initializing .NET user secrets..."
    try {
        dotnet user-secrets init --project $projectPath.FullName 2>$null
    }
    catch {
        # Already initialized, continue
    }
    
    # Get or set Key Vault endpoint
    Write-Information "INFO: Configuring Key Vault endpoint..."
    $keyVaultEndpoint = Get-OrSet-UserSecret -Key "KeyVault:Endpoint" -ProjectPath $projectPath.FullName
    
    # Extract Key Vault name from endpoint
    $keyVaultName = $keyVaultEndpoint -replace "https://", "" -replace "\.vault\.azure\.net/?", ""
    if ([string]::IsNullOrWhiteSpace($keyVaultName)) {
        throw "Invalid Key Vault endpoint format. Expected: https://your-keyvault.vault.azure.net/"
    }
    Write-Information "INFO: Key Vault name: $keyVaultName"
    
    # Retrieve all secrets from Key Vault
    $secrets = Get-KeyVaultSecrets -KeyVaultName $keyVaultName
    
    # Find resource group and app name from BaseUri-Production
    if ($secrets.ContainsKey("BaseUri-Production")) {
        $productionUri = $secrets["BaseUri-Production"]
        # Extract app name from URL like https://app-name.azurewebsites.net
        if ($productionUri -match "https://([^.]+)\.azurewebsites\.net") {
            $appName = $matches[1]
            Write-Information "INFO: Detected app name: $appName"
        }
        else {
            throw "Could not parse app name from BaseUri-Production: $productionUri"
        }
    }
    else {
        throw "BaseUri-Production not found in Key Vault"
    }
    
    # Find resource group
    Write-Information "INFO: Finding resource group for app: $appName"
    $webApps = az webapp list --output json | ConvertFrom-Json
    $targetApp = $webApps | Where-Object { $_.name -eq $appName }
    if (!$targetApp) {
        throw "Web app '$appName' not found in your Azure subscription"
    }
    $resourceGroup = $targetApp.resourceGroup
    Write-Information "INFO: Resource group: $resourceGroup"
    
    # Confirmation
    if (!$Force) {
        Write-Information ""
        Write-Information "Configuration Summary:"
        Write-Information "  App Name: $appName"
        Write-Information "  Resource Group: $resourceGroup"
        Write-Information "  Key Vault: $keyVaultName"
        Write-Information "  Environment: Development"
        Write-Information "  Features: Monitoring enabled, Swagger enabled"
        Write-Information ""
        $confirm = Read-Host "Continue with Development environment switch? (y/N)"
        if ($confirm -notmatch "^[yY]") {
            Write-Information "INFO: Operation cancelled"
            exit 0
        }
    }
    
    # Switch to Development environment
    Write-Information ""
    Write-Information "INFO: Switching to Development environment..."
    az webapp config appsettings set --resource-group $resourceGroup --name $appName --settings ASPNETCORE_ENVIRONMENT=Development | Out-Null
    Write-Information "SUCCESS: Environment set to Development"
    
    # Set Health Check API Key from Key Vault
    if ($secrets.ContainsKey("HealthCheckApiKey")) {
        Write-Information "INFO: Configuring Health Check API Key..."
        az webapp config appsettings set --resource-group $resourceGroup --name $appName --settings HealthCheckApiKey=$($secrets["HealthCheckApiKey"]) | Out-Null
        Write-Information "SUCCESS: Health Check API Key configured"
    }
    else {
        Write-Warning "WARNING: HealthCheckApiKey not found in Key Vault"
    }
    
    # Restart the application
    Write-Information "INFO: Restarting application..."
    az webapp restart --resource-group $resourceGroup --name $appName | Out-Null
    Write-Information "SUCCESS: Application restarted"
    Write-Information ""
    Write-Information "NOTE: After restart, the application may return 503 errors for 30-60 seconds"
    Write-Information "      while it initializes. This is normal behavior during startup."
    
    # Wait for startup
    Write-Information "INFO: Waiting for application startup (this may take 30-60 seconds)..."
    Write-Information "NOTE: 503 Service Unavailable errors during startup are normal and expected"
    Start-Sleep -Seconds 20
    
    # Test the application
    $appUrl = "https://$appName.azurewebsites.net"
    Write-Information "INFO: Testing application at: $appUrl"
    
    try {
        $response = Invoke-RestMethod -Uri $appUrl -Method Get -TimeoutSec 30
        if ($response -match "Hello Azure Communication Services") {
            Write-Information "SUCCESS: Application is responding correctly"
        }
        else {
            Write-Warning "WARNING: Application responded but with unexpected content"
        }
    }
    catch {
        if ($_.Exception.Message -match "503") {
            Write-Information "INFO: Application still starting up (503 error is normal during startup)"
        }
        else {
            Write-Warning "WARNING: Could not test application: $($_.Exception.Message)"
        }
    }
    
    # Test monitoring endpoints if Health Check API Key is available
    if ($secrets.ContainsKey("HealthCheckApiKey")) {
        Write-Information "INFO: Testing secured monitoring endpoints..."
        $apiKey = $secrets["HealthCheckApiKey"]
        $headers = @{ "X-API-Key" = $apiKey }
        
        try {
            $healthResponse = Invoke-RestMethod -Uri "$appUrl/health" -Headers $headers -Method Get -TimeoutSec 30
            Write-Information "SUCCESS: Health endpoint accessible with API key"
            
            $swaggerResponse = Invoke-WebRequest -Uri "$appUrl/swagger" -Headers $headers -Method Get -TimeoutSec 30
            if ($swaggerResponse.StatusCode -eq 200 -or $swaggerResponse.StatusCode -eq 301) {
                Write-Information "SUCCESS: Swagger documentation accessible with API key"
            }
        }
        catch {
            if ($_.Exception.Message -match "503") {
                Write-Information "INFO: Monitoring endpoints still starting up (503 error is normal during startup)"
            }
            else {
                Write-Warning "WARNING: Could not test monitoring endpoints: $($_.Exception.Message)"
            }
        }
    }
    
    Write-Information ""
    Write-Information "Development Environment Setup Complete!"
    Write-Information ("=" * 60)
    Write-Information "Application URL: $appUrl"
    Write-Information "Swagger Docs: $appUrl/swagger (requires X-API-Key header)"
    Write-Information "Health Check: $appUrl/health (requires X-API-Key header)"
    Write-Information "Monitoring: All endpoints enabled with API key authentication"
    Write-Information ""
    Write-Information "Ready for development and debugging!"
    
    # Display API key for easy access
    if ($secrets.ContainsKey("HealthCheckApiKey")) {
        Write-Information "API Key for testing: $($secrets["HealthCheckApiKey"])"
        Write-Information "   Use with: curl -H 'X-API-Key: $($secrets["HealthCheckApiKey"])' $appUrl/health"
    }
    
}
catch {
    Write-Error "ERROR: Failed to switch to Development environment: $($_.Exception.Message)"
    exit 1
}