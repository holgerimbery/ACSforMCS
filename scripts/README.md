# Environment Management Scripts

This folder contains PowerShell scripts for automated environment management and deployment of the ACS for MCS application.

## Scripts Overview

### Resource Creation
- **`create-azure-resources.ps1`** - **🚀 New Projects**: Create all required Azure resources with name validation

### Core Configuration
- **`setup-configuration.ps1`** - **🔧 Start Here**: Initialize and validate local configuration
- **`show-environment.ps1`** - Display current environment status and configuration

### Environment Management  
- **`switch-to-development.ps1`** - Configure application for development with monitoring endpoints
- **`switch-to-production.ps1`** - Configure application for production with security optimizations

### Deployment
- **`deploy-application.ps1`** - Build and deploy application to Azure Web App

## Quick Start Workflow

### For New Projects (No Azure Resources Yet)
```powershell
# 1. Create all required Azure resources
.\scripts\create-azure-resources.ps1 -ApplicationName "myproject"

# 2. Initialize configuration using the created resources
.\scripts\setup-configuration.ps1 -KeyVaultName "kv-myproject-prod"

# 3. Deploy application
.\scripts\deploy-application.ps1
```

### First Time Setup (Existing Azure Resources)
```powershell
# 1. Initialize configuration (auto-detects Key Vault or prompts for selection)
.\scripts\setup-configuration.ps1

# 2. Validate everything is working
.\scripts\setup-configuration.ps1 -ValidateOnly

# 3. Check environment status
.\scripts\show-environment.ps1

# 4. Deploy application
.\scripts\deploy-application.ps1
```

### Daily Usage
```powershell
# Check current status
.\scripts\show-environment.ps1

# Switch to development (enables monitoring endpoints)
.\scripts\switch-to-development.ps1

# Switch to production (disables monitoring for security)
.\scripts\switch-to-production.ps1

# Deploy after changes
.\scripts\deploy-application.ps1
```

## Configuration Requirements

All scripts require:
- **Azure CLI**: Authenticated with appropriate permissions
- **.NET SDK**: For user secrets management
- **PowerShell 7.0+**: Modern PowerShell version
- **Key Vault Access**: Reader + Key Vault Secrets User roles

Required Key Vault secrets:
- `AcsConnectionString` - Azure Communication Services connection
- `DirectLineSecret` - Microsoft Bot Framework DirectLine secret  
- `BaseUri-Production` - Production app URL
- `BaseUri-Development` - Development environment URL
- `AgentPhoneNumber` - Phone number for call transfers
- `CognitiveServiceEndpoint` - Azure Cognitive Services endpoint
- `HealthCheckApiKey` - API key for monitoring endpoints

## Features

### � **create-azure-resources.ps1**
- Creates all required Azure resources for ACS for MCS
- Validates name availability before resource creation
- Follows established naming conventions (kv-, asp-, app-, etc.)
- Checks Azure Web App name globally for availability
- Configures optimal settings for .NET 9.0 applications
- Provides connection strings and endpoints for immediate use
- Saves configuration information for setup scripts
- Validation-only mode to test names without creating resources

**Usage Examples:**
```powershell
# Create resources for new project
.\scripts\create-azure-resources.ps1 -ApplicationName "myproject"

# Create in different region/environment
.\scripts\create-azure-resources.ps1 -ApplicationName "myapp" -Location "East US" -Environment "dev"

# Test name availability only
.\scripts\create-azure-resources.ps1 -ApplicationName "test" -ValidateOnly

# Skip confirmations
.\scripts\create-azure-resources.ps1 -ApplicationName "prod" -Force
```

**Resources Created:**
- Resource Group: `rg-{app}-{env}`
- Key Vault: `kv-{app}-{env}`
- Azure Communication Services: `acs-{app}-{env}`
- Cognitive Services: `cog-speech-{app}-{env}`
- App Service Plan: `asp-{app}-{env}`
- Web App: `app-{app}-{env}`

### �🔧 **setup-configuration.ps1**
- Auto-detects existing Key Vault configuration from user secrets
- Interactive Key Vault selection if multiple vaults available
- Validates access to all required secrets
- Initializes .NET user secrets for local development
- Validation-only mode for troubleshooting
- Force overwrite option for reconfiguration

### 📊 **show-environment.ps1**  
- Displays current environment mode (Development/Production)
- Shows key configuration settings and endpoints
- Tests endpoint accessibility and health
- Provides API key access instructions (without exposing values)
- Comprehensive environment status summary

### 🛠️ **switch-to-development.ps1**
- Enables all monitoring endpoints (`/health`, `/health/calls`, `/health/config`, `/health/metrics`)
- Enables Swagger documentation at `/swagger`
- Secures all endpoints with API key authentication
- Optimizes for debugging and development workflow
- Tests endpoints after configuration

### 🚀 **switch-to-production.ps1**
- Disables all monitoring endpoints for security
- Disables Swagger documentation
- Applies production performance optimizations
- Validates production readiness
- Maintains only core ACS webhook endpoints

### 📦 **deploy-application.ps1**
- Retrieves all configuration from Azure Key Vault
- Builds application in Release mode with optimizations
- Creates and deploys application package
- Verifies deployment success and endpoint accessibility
- Supports both Development and Production environments
- Provides deployment summary and testing information

### 🔧 **fix-keyvault-access.ps1** (Troubleshooting Tool)
- Emergency troubleshooting tool for Key Vault access issues
- Fixes Key Vault access policy problems after ARM deployment
- Adds user permissions to Key Vault secrets (get, list, set, delete)
- Used when ARM deployment access policies fail or for post-deployment user additions
- Validates Object ID format and provides clear feedback
- Includes guided next steps for verification

**Usage:**
```powershell
# Get your Object ID and fix Key Vault access
$objectId = az ad signed-in-user show --query id -o tsv
.\scripts\fix-keyvault-access.ps1 -KeyVaultName "kv-yourproject-prod" -UserObjectId $objectId
```

**When to use:**
- ARM deployment failed to set Key Vault access policies correctly
- "Unauthorized" errors when accessing Key Vault secrets
- Need to add additional users to Key Vault access after deployment
- Emergency access restoration for Key Vault secrets

## Environment Differences

| Feature | Development | Production |
|---------|-------------|------------|
| **Monitoring Endpoints** | ✅ Enabled (API key protected) | ❌ Disabled |
| **Swagger Documentation** | ✅ Enabled (API key protected) | ❌ Disabled |
| **Core ACS Webhooks** | ✅ Enabled | ✅ Enabled |
| **Performance Optimization** | 🔸 Basic | 🔥 Full optimization |
| **Security Posture** | 🛡️ API Key protected | 🔒 Minimal attack surface |
| **Debugging Support** | ✅ Full logging | 🎯 Production logging |

## Security Features

- **No sensitive data exposure**: API keys and secrets never displayed in console output
- **Secure configuration management**: All sensitive values stored in Azure Key Vault
- **User secrets integration**: Local development uses .NET user secrets (not source control)
- **Principle of least privilege**: Production mode disables unnecessary endpoints
- **Audit trail**: All configuration changes logged and tracked

## Troubleshooting

### Common Issues & Solutions

**Configuration not found:**
```powershell
# Run setup to initialize configuration
.\scripts\setup-configuration.ps1 -Force
```

**Azure CLI authentication:**
```powershell
# Re-authenticate with Azure
az login
az account show
```

**Key Vault access denied:**
```powershell
# Fix Key Vault access policies
$objectId = az ad signed-in-user show --query id -o tsv
.\scripts\fix-keyvault-access.ps1 -KeyVaultName "kv-yourproject-prod" -UserObjectId $objectId
```

**Alternative: Verify you have proper RBAC roles:**
- Verify you have `Reader` + `Key Vault Secrets User` roles
- Check if Key Vault uses RBAC (not legacy access policies)
- Verify you have `Reader` + `Key Vault Secrets User` roles
- Check if Key Vault uses RBAC (not legacy access policies)

**User secrets not working:**
```powershell
# Clear and reinitialize user secrets
dotnet user-secrets clear --project ACSforMCS.csproj
.\scripts\setup-configuration.ps1 -Force
```

**Deployment failures:**
```powershell
# Validate configuration first
.\scripts\setup-configuration.ps1 -ValidateOnly

# Check app service and resource group access
az webapp list --query "[].{name:name,resourceGroup:resourceGroup}" --output table
```

## Documentation

**📚 Complete Documentation**: [Environment Management Scripts Wiki Page](../.wiki/Environment-Management-Scripts.md)

**📖 Additional Resources**:
- [Azure Web App Deployment Guide](../.wiki/Azure-Web-App-Deployment.md)
- [Local Development Setup](../.wiki/Local-Development.md)  
- [Prerequisites and Setup](../.wiki/Prerequisites-and-Setup.md)
- [Swagger API Documentation](../.wiki/Swagger-API-Documentation.md)

## Help & Support

```powershell
# Get detailed help for any script
Get-Help .\scripts\setup-configuration.ps1 -Full
Get-Help .\scripts\show-environment.ps1 -Full
Get-Help .\scripts\switch-to-development.ps1 -Full
Get-Help .\scripts\switch-to-production.ps1 -Full
Get-Help .\scripts\deploy-application.ps1 -Full
```

---

**🚀 Ready to get started?** Run `.\scripts\setup-configuration.ps1` to initialize your environment!