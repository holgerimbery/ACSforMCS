#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Compiles and deploys the ACS for MCS application to Azure Web App
.DESCRIPTION
    This script automates the build and deployment process by:
    - Retrieving configuration from Azure Key Vault
    - Building the .NET application in Release mode
    - Creating a deployment package
    - Deploying to the Azure Web App
    - Verifying deployment success
.PARAMETER Environment
    Target environment for deployment (Development or Production). If not specified, uses current environment.
.PARAMETER Force
    Skip confirmation prompts
.PARAMETER SkipBuild
    Skip the build process and deploy existing artifacts
.EXAMPLE
    .\deploy-application.ps1
    .\deploy-application.ps1 -Environment Production -Force
    .\deploy-application.ps1 -SkipBuild -Force
#>

param(
    [ValidateSet("Development", "Production", "")]
    [string]$Environment = "",
    [switch]$Force,
    [switch]$SkipBuild
)

# Script configuration
$ErrorActionPreference = "Stop"
$InformationPreference = "Continue"

Write-Information "ACS for MCS - Build and Deploy"
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
    Write-Information "INFO: Retrieving Key Vault configuration..."
    $keyVaultEndpoint = dotnet user-secrets list --project $projectPath.FullName | Where-Object { $_ -match "KeyVault:Endpoint\s*=\s*(.+)" }
    if (!$keyVaultEndpoint) {
        throw "KeyVault:Endpoint not found in user secrets. Please run setup script first."
    }
    
    $keyVaultName = ($keyVaultEndpoint -split '=')[1].Trim() -replace 'https://([^.]+)\..*', '$1'
    Write-Information "INFO: Key Vault: $keyVaultName"
    
    # Retrieve all secrets from Key Vault
    Write-Information "INFO: Retrieving deployment configuration from Key Vault..."
    $secrets = @{}
    $secretNames = az keyvault secret list --vault-name $keyVaultName --query "[].name" --output tsv
    if (!$secretNames) {
        throw "No secrets found in Key Vault or access denied"
    }
    
    foreach ($secretName in $secretNames) {
        try {
            $secretValue = az keyvault secret show --vault-name $keyVaultName --name $secretName --query "value" --output tsv
            $secrets[$secretName] = $secretValue
            Write-Information "  SUCCESS: Retrieved: $secretName"
        }
        catch {
            Write-Warning "  WARNING: Could not retrieve: $secretName - $($_.Exception.Message)"
        }
    }
    
    # Determine target environment
    if ([string]::IsNullOrEmpty($Environment)) {
        # Detect current environment from app configuration
        $appName = $keyVaultName -replace '^kv-', '' -replace '-.*$', ''
        $resourceGroup = az webapp list --query "[?name=='$appName'].resourceGroup" --output tsv
        if ($resourceGroup) {
            $currentEnv = az webapp config appsettings list --name $appName --resource-group $resourceGroup --query "[?name=='ASPNETCORE_ENVIRONMENT'].value" --output tsv
            if ($currentEnv) {
                $Environment = $currentEnv
                Write-Information "INFO: Detected current environment: $Environment"
            } else {
                $Environment = "Production"
                Write-Information "INFO: No environment setting found, defaulting to: $Environment"
            }
        } else {
            $Environment = "Production"
            Write-Information "INFO: Could not detect environment, defaulting to: $Environment"
        }
    } else {
        Write-Information "INFO: Target environment specified: $Environment"
    }
    
    # Extract deployment configuration
    $baseUriKey = "BaseUri-$Environment"
    if (!$secrets.ContainsKey($baseUriKey)) {
        throw "Required secret '$baseUriKey' not found in Key Vault"
    }
    
    $appUrl = $secrets[$baseUriKey]
    $appName = ($appUrl -replace 'https://([^.]+)\..*', '$1')
    if (!$appName) {
        throw "Could not extract app name from URL: $appUrl"
    }
    Write-Information "SUCCESS: Target application: $appName"
    Write-Information "INFO: Target URL: $appUrl"
    
    # Find resource group
    Write-Information "INFO: Finding resource group for app: $appName"
    $resourceGroup = az webapp list --query "[?name=='$appName'].resourceGroup" --output tsv
    if (!$resourceGroup) {
        throw "Could not find resource group for app: $appName"
    }
    Write-Information "INFO: Resource group: $resourceGroup"
    
    # Validate required secrets
    $requiredSecrets = @("AcsConnectionString", "DirectLineSecret", "CognitiveServiceEndpoint", "AgentPhoneNumber")
    $missingSecrets = @()
    foreach ($secret in $requiredSecrets) {
        if (!$secrets.ContainsKey($secret)) {
            $missingSecrets += $secret
        }
    }
    
    if ($missingSecrets.Count -gt 0) {
        throw "Missing required secrets in Key Vault: $($missingSecrets -join ', ')"
    }
    
    Write-Information "SUCCESS: All required secrets found in Key Vault"
    
    # Build application
    if (!$SkipBuild) {
        Write-Information ""
        Write-Information "INFO: Building application in Release mode..."
        
        # Clean previous builds
        Write-Information "INFO: Cleaning previous build artifacts..."
        dotnet clean $projectPath.FullName --configuration Release --verbosity quiet
        
        # Restore packages
        Write-Information "INFO: Restoring NuGet packages..."
        dotnet restore $projectPath.FullName --verbosity quiet
        
        # Build application
        Write-Information "INFO: Compiling application..."
        $buildResult = dotnet build $projectPath.FullName --configuration Release --no-restore --verbosity quiet
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed. Exit code: $LASTEXITCODE"
        }
        Write-Information "SUCCESS: Application built successfully"
        
        # Publish application
        Write-Information "INFO: Publishing application for deployment..."
        $publishPath = Join-Path $PSScriptRoot "..\bin\publish"
        if (Test-Path $publishPath) {
            Remove-Item $publishPath -Recurse -Force
        }
        
        dotnet publish $projectPath.FullName --configuration Release --output $publishPath --no-build --verbosity quiet
        if ($LASTEXITCODE -ne 0) {
            throw "Publish failed. Exit code: $LASTEXITCODE"
        }
        Write-Information "SUCCESS: Application published to: $publishPath"
        
        # Create deployment package
        Write-Information "INFO: Creating deployment package..."
        $deploymentZip = Join-Path $PSScriptRoot "..\deployment-$(Get-Date -Format 'yyyyMMdd-HHmmss').zip"
        
        # Compress published files
        Compress-Archive -Path "$publishPath\*" -DestinationPath $deploymentZip -Force
        Write-Information "SUCCESS: Deployment package created: $deploymentZip"
        
        # Verify package contents
        $zipInfo = Get-Item $deploymentZip
        Write-Information "INFO: Package size: $([math]::Round($zipInfo.Length / 1MB, 2)) MB"
    } else {
        Write-Information "INFO: Skipping build process as requested"
        
        # Find most recent deployment package
        $deploymentPackages = Get-ChildItem -Path (Split-Path $PSScriptRoot) -Filter "deployment-*.zip" | Sort-Object LastWriteTime -Descending
        if ($deploymentPackages.Count -eq 0) {
            throw "No deployment packages found. Run without -SkipBuild to create a new package."
        }
        
        $deploymentZip = $deploymentPackages[0].FullName
        Write-Information "INFO: Using existing deployment package: $deploymentZip"
    }
    
    # Deployment confirmation
    if (!$Force) {
        Write-Information ""
        Write-Information "DEPLOYMENT SUMMARY:"
        Write-Information "  Source: $($projectPath.Name)"
        Write-Information "  Target: $appName ($Environment environment)"
        Write-Information "  URL: $appUrl"
        Write-Information "  Resource Group: $resourceGroup"
        Write-Information "  Package: $(Split-Path $deploymentZip -Leaf)"
        Write-Information ""
        Write-Warning "WARNING: This will deploy to $Environment environment!"
        Write-Information ""
        
        $confirm = Read-Host "Continue with deployment? (y/N)"
        if ($confirm -ne "y" -and $confirm -ne "Y") {
            Write-Information "INFO: Deployment cancelled by user"
            return
        }
    }
    
    # Deploy to Azure Web App
    Write-Information ""
    Write-Information "INFO: Deploying to Azure Web App..."
    Write-Information "INFO: Target: $appName in $resourceGroup"
    
    $deploymentStart = Get-Date
    az webapp deploy --name $appName --resource-group $resourceGroup --src-path $deploymentZip --type zip --async false
    
    if ($LASTEXITCODE -ne 0) {
        throw "Deployment failed. Exit code: $LASTEXITCODE"
    }
    
    $deploymentDuration = (Get-Date) - $deploymentStart
    Write-Information "SUCCESS: Deployment completed in $([math]::Round($deploymentDuration.TotalSeconds, 1)) seconds"
    
    # Wait for application startup
    Write-Information ""
    Write-Information "INFO: Waiting for application startup..."
    Write-Information "NOTE: The application may return 503 errors for 30-60 seconds while initializing"
    
    $maxRetries = 12
    $retryCount = 0
    $appReady = $false
    
    do {
        Start-Sleep -Seconds 10
        $retryCount++
        
        try {
            $response = Invoke-WebRequest -Uri $appUrl -Method Get -TimeoutSec 15 -ErrorAction SilentlyContinue
            if ($response.StatusCode -eq 200) {
                $appReady = $true
                Write-Information "SUCCESS: Application is responding (Status: $($response.StatusCode))"
                break
            }
        }
        catch {
            $statusCode = $_.Exception.Response.StatusCode
            if ($statusCode -eq 503) {
                Write-Information "INFO: Application starting up (503 - attempt $retryCount/$maxRetries)"
            } else {
                Write-Information "INFO: Application status: $statusCode (attempt $retryCount/$maxRetries)"
            }
        }
    } while ($retryCount -lt $maxRetries)
    
    if (!$appReady) {
        Write-Warning "WARNING: Application may still be starting up. Check Azure portal for deployment status."
    }
    
    # Verify core endpoints
    Write-Information ""
    Write-Information "INFO: Verifying core endpoints..."
    
    # Test webhook endpoint
    try {
        $webhookResponse = Invoke-WebRequest -Uri "$appUrl/api/incomingCall" -Method Post -Body '{}' -ContentType "application/json" -TimeoutSec 15
        if ($webhookResponse.StatusCode -eq 400) {
            Write-Information "SUCCESS: Webhook endpoint accessible (400 expected for empty payload)"
        }
    }
    catch {
        if ($_.Exception.Response.StatusCode -eq 400) {
            Write-Information "SUCCESS: Webhook endpoint accessible (400 expected for empty payload)"
        } else {
            Write-Warning "WARNING: Could not verify webhook endpoint: $($_.Exception.Message)"
        }
    }
    
    # Environment-specific endpoint verification
    if ($Environment -eq "Development") {
        Write-Information "INFO: Verifying development endpoints..."
        $healthCheckApiKey = $secrets["HealthCheckApiKey"]
        
        if ($healthCheckApiKey) {
            try {
                $headers = @{"X-API-Key" = $healthCheckApiKey}
                $healthResponse = Invoke-WebRequest -Uri "$appUrl/health" -Method Get -Headers $headers -TimeoutSec 15 -ErrorAction SilentlyContinue
                if ($healthResponse.StatusCode -eq 200) {
                    Write-Information "SUCCESS: Health endpoint accessible with API key"
                }
            }
            catch {
                Write-Warning "WARNING: Could not verify health endpoint: $($_.Exception.Message)"
            }
        }
    } else {
        Write-Information "INFO: Verifying production security..."
        try {
            $healthResponse = Invoke-WebRequest -Uri "$appUrl/health" -Method Get -TimeoutSec 10 -ErrorAction SilentlyContinue
            Write-Warning "WARNING: Health endpoint accessible (should be disabled in production)"
        }
        catch {
            if ($_.Exception.Response.StatusCode -eq 404) {
                Write-Information "SUCCESS: Health endpoint properly disabled"
            }
        }
    }
    
    # Cleanup deployment package (optional)
    if (!$SkipBuild) {
        $cleanup = $Force
        if (!$Force) {
            $cleanupConfirm = Read-Host "Delete deployment package? (y/N)"
            $cleanup = ($cleanupConfirm -eq "y" -or $cleanupConfirm -eq "Y")
        }
        
        if ($cleanup) {
            Remove-Item $deploymentZip -Force
            Write-Information "INFO: Deployment package cleaned up"
        } else {
            Write-Information "INFO: Deployment package retained: $deploymentZip"
        }
    }
    
    # Deployment summary
    Write-Information ""
    Write-Information "SUCCESS: Deployment Complete!"
    Write-Information ("=" * 60)
    Write-Information "Environment: $Environment"
    Write-Information "Application: $appUrl"
    Write-Information "Resource Group: $resourceGroup"
    Write-Information "Deployment Duration: $([math]::Round($deploymentDuration.TotalSeconds, 1)) seconds"
    Write-Information ""
    Write-Information "AVAILABLE ENDPOINTS:"
    Write-Information "  GET  $appUrl/ - Welcome message"
    Write-Information "  POST $appUrl/api/incomingCall - ACS incoming call webhook"
    Write-Information "  POST $appUrl/api/calls/{contextId} - ACS call events webhook"
    
    if ($Environment -eq "Development" -and $secrets.ContainsKey("HealthCheckApiKey")) {
        Write-Information "  GET  $appUrl/health - Health check (requires X-API-Key header)"
        Write-Information "  GET  $appUrl/swagger - API documentation (requires X-API-Key header)"
        Write-Information ""
        Write-Information "API Key for testing: $($secrets["HealthCheckApiKey"])"
    }
    
    Write-Information ""
    Write-Information "INFO: Use show-environment.ps1 to verify deployment status"
    Write-Information "INFO: Monitor application logs through Azure portal"
    
    if ($secrets.ContainsKey("AgentPhoneNumber")) {
        Write-Information ""
        Write-Information "READY FOR TESTING:"
        Write-Information "  Agent Phone: $($secrets["AgentPhoneNumber"])"
        Write-Information "  Configure Event Grid to send events to: $appUrl/api/incomingCall"
    }
    
}
catch {
    Write-Error "ERROR: Deployment failed: $($_.Exception.Message)"
    exit 1
}