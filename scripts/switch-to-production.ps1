#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Switches the ACS for MCS application to Production environment
.DESCRIPTION
    This script automatically configures the Azure Web App for Production environment by:
    - Retrieving configuration from Azure Key Vault
    - Setting up .NET user secrets for local development
    - Switching the web app environment to Production
    - Disabling monitoring endpoints and Swagger for security and performance
.PARAMETER Force
    Skip confirmation prompts
.EXAMPLE
    .\switch-to-production.ps1
    .\switch-to-production.ps1 -Force
#>

param(
    [switch]$Force
)

# Script configuration
$ErrorActionPreference = "Stop"
$InformationPreference = "Continue"

Write-Information "ACS for MCS - Switching to Production Environment"
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

# Function to validate production readiness
function Test-ProductionReadiness {
    param(
        [hashtable]$Secrets,
        [string]$AppName,
        [string]$ResourceGroup
    )
    
    Write-Information "INFO: Validating production readiness..."
    $issues = @()
    
    # Check required secrets
    $requiredSecrets = @(
        "AcsConnectionString",
        "DirectLineSecret", 
        "BaseUri-Production",
        "AgentPhoneNumber",
        "CognitiveServiceEndpoint",
        "HealthCheckApiKey"
    )
    
    foreach ($secret in $requiredSecrets) {
        if (!$Secrets.ContainsKey($secret) -or [string]::IsNullOrWhiteSpace($Secrets[$secret])) {
            $issues += "Missing or empty secret: $secret"
        }
    }
    
    # Validate BaseUri-Production format
    if ($Secrets.ContainsKey("BaseUri-Production")) {
        $prodUri = $Secrets["BaseUri-Production"]
        if ($prodUri -notmatch "^https://.*\.azurewebsites\.net/?$") {
            $issues += "BaseUri-Production should be an Azure Web App URL (https://app-name.azurewebsites.net)"
        }
    }
    
    # Check Azure Web App configuration
    try {
        $appConfig = az webapp config show --name $AppName --resource-group $ResourceGroup --output json | ConvertFrom-Json
        
        if ($appConfig.alwaysOn -ne $true) {
            $issues += "Azure Web App 'Always On' should be enabled for production"
        }
        
        if ($appConfig.webSocketsEnabled -ne $true) {
            $issues += "Azure Web App 'Web Sockets' should be enabled for real-time communication"
        }
        
        if ($appConfig.httpsOnly -ne $true) {
            $issues += "Azure Web App 'HTTPS Only' should be enabled for security"
        }
    }
    catch {
        $issues += "Could not validate Azure Web App configuration: $($_.Exception.Message)"
    }
    
    return $issues
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
            Write-Information "SUCCESS: Detected app name: $appName"
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
    
    # Validate production readiness
    $validationIssues = Test-ProductionReadiness -Secrets $secrets -AppName $appName -ResourceGroup $resourceGroup
    
    if ($validationIssues.Count -gt 0) {
        Write-Warning "WARNING: Production readiness validation found issues:"
        foreach ($issue in $validationIssues) {
            Write-Warning "  ERROR: $issue"
        }
        
        if (!$Force) {
            Write-Information ""
            $continueAnyway = Read-Host "Continue with production deployment despite validation issues? (y/N)"
            if ($continueAnyway -notmatch "^[yY]") {
                Write-Information "INFO: Operation cancelled due to validation issues"
                exit 1
            }
        }
    }
    else {
        Write-Information "SUCCESS: Production readiness validation passed"
    }
    
    # Confirmation
    if (!$Force) {
        Write-Information ""
        Write-Information "INFO: Production Configuration Summary:"
        Write-Information "  App Name: $appName"
        Write-Information "  Resource Group: $resourceGroup"
        Write-Information "  Key Vault: $keyVaultName"
        Write-Information "  Environment: Production"
        Write-Information "  Features: Monitoring disabled, Swagger disabled, Performance optimized"
        Write-Information ""
        Write-Warning "WARNING: This will switch to PRODUCTION environment!"
        Write-Information "   - All monitoring endpoints will be disabled"
        Write-Information "   - Swagger documentation will be disabled"
        Write-Information "   - Only core ACS webhooks will be available"
        Write-Information ""
        $confirm = Read-Host "Continue with Production environment switch? (y/N)"
        if ($confirm -notmatch "^[yY]") {
            Write-Information "INFO: Operation cancelled"
            exit 0
        }
    }
    
    # Switch to Production environment
    Write-Information ""
    Write-Information "INFO: Switching to Production environment..."
    az webapp config appsettings set --resource-group $resourceGroup --name $appName --settings ASPNETCORE_ENVIRONMENT=Production | Out-Null
    Write-Information "SUCCESS: Environment set to Production"
    
    # Set Health Check API Key from Key Vault (even though endpoints are disabled, it's good practice)
    if ($secrets.ContainsKey("HealthCheckApiKey")) {
        Write-Information "INFO: Configuring Health Check API Key..."
        az webapp config appsettings set --resource-group $resourceGroup --name $appName --settings HealthCheckApiKey=$($secrets["HealthCheckApiKey"]) | Out-Null
        Write-Information "SUCCESS: Health Check API Key configured (for future use)"
    }
    
    # Optimize for production
    Write-Information "INFO: Applying production optimizations..."
    
    # Ensure critical settings are enabled
    az webapp config set --resource-group $resourceGroup --name $appName --always-on true --web-sockets-enabled true --http20-enabled true | Out-Null
    Write-Information "SUCCESS: Always On, WebSockets, and HTTP/2 enabled"
    
    # Restart the application
    Write-Information "INFO: Restarting application..."
    az webapp restart --resource-group $resourceGroup --name $appName | Out-Null
    Write-Information "SUCCESS: Application restarted"
    Write-Information ""
    Write-Information "NOTE: After restart, the application may return 503 errors for 30-60 seconds"
    Write-Information "      while it initializes. This is normal behavior during startup."
    
    # Wait for startup
    Write-Information "⏳ Waiting for application startup..."
    Start-Sleep -Seconds 25
    
    # Test the application
    $appUrl = "https://$appName.azurewebsites.net"
    Write-Information "INFO: Testing production application at: $appUrl"
    
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
        Write-Warning "WARNING: Could not test application: $($_.Exception.Message)"
    }
    
    # Verify monitoring endpoints are disabled
    Write-Information "INFO: Verifying monitoring endpoints are disabled..."
    try {
        $healthResponse = Invoke-WebRequest -Uri "$appUrl/health" -Method Get -TimeoutSec 15 -ErrorAction SilentlyContinue
        if ($healthResponse.StatusCode -eq 404) {
            Write-Information "SUCCESS: Health endpoint properly disabled"
        }
        else {
            Write-Warning "WARNING: Health endpoint may still be accessible (Status: $($healthResponse.StatusCode))"
        }
    }
    catch {
        if ($_.Exception.Response.StatusCode -eq 404) {
            Write-Information "SUCCESS: Health endpoint properly disabled"
        }
        else {
            Write-Warning "WARNING: Could not verify health endpoint status: $($_.Exception.Message)"
        }
    }
    
    try {
        $swaggerResponse = Invoke-WebRequest -Uri "$appUrl/swagger" -Method Get -TimeoutSec 15 -ErrorAction SilentlyContinue
        if ($swaggerResponse.StatusCode -eq 404) {
            Write-Information "SUCCESS: Swagger documentation properly disabled"
        }
        else {
            Write-Warning "WARNING: Swagger may still be accessible (Status: $($swaggerResponse.StatusCode))"
        }
    }
    catch {
        if ($_.Exception.Response.StatusCode -eq 404) {
            Write-Information "SUCCESS: Swagger documentation properly disabled"
        }
        else {
            Write-Warning "WARNING: Could not verify swagger status: $($_.Exception.Message)"
        }
    }
    
    # Test core webhook endpoints
    Write-Information "INFO: Testing core webhook endpoints..."
    try {
        $webhookResponse = Invoke-WebRequest -Uri "$appUrl/api/incomingCall" -Method Post -Body '{}' -ContentType "application/json" -TimeoutSec 15
        if ($webhookResponse.StatusCode -eq 400) {
            Write-Information "SUCCESS: Incoming call webhook endpoint accessible (400 expected for empty payload)"
        }
    }
    catch {
        if ($_.Exception.Response.StatusCode -eq 400) {
            Write-Information "SUCCESS: Incoming call webhook endpoint accessible (400 expected for empty payload)"
        }
        else {
            Write-Warning "WARNING: Could not test webhook endpoint: $($_.Exception.Message)"
        }
    }
    
    Write-Information ""
    Write-Information "SUCCESS: Production Environment Setup Complete!"
    Write-Information ("=" * 60)
    Write-Information "INFO: Application URL: $appUrl"
    Write-Information "INFO: Webhook Endpoint: $appUrl/api/incomingCall"
    Write-Information "INFO: Security: Monitoring disabled, Swagger disabled"
    Write-Information "INFO: Performance: Optimized for production workloads"
    Write-Information ""
    Write-Information "INFO: Available Endpoints in Production:"
    Write-Information "   GET  / - Welcome message"
    Write-Information "   POST /api/incomingCall - ACS incoming call webhook"
    Write-Information "   POST /api/calls/{contextId} - ACS call events webhook"
    Write-Information ""
    Write-Information "SUCCESS: Production deployment ready!"
    Write-Information "INFO: Next steps:"
    Write-Information "   1. Configure Event Grid to send events to: $appUrl/api/incomingCall"
    Write-Information "   2. Test phone calls to your ACS number"
    Write-Information "   3. Monitor application through Azure portal"
    
    if ($secrets.ContainsKey("AgentPhoneNumber")) {
        Write-Information "   4. Agent phone number for transfers: $($secrets["AgentPhoneNumber"])"
    }
    
}
catch {
    Write-Error "ERROR: Failed to switch to Production environment: $($_.Exception.Message)"
    exit 1
}