# ARM Template Deployment Guide

## Interactive Azure Portal Deployment

The ARM template provides a user-friendly deployment experience through the Azure Portal with the following interactive features:

### Step 1: Basic Configuration
- **Project Name**: Enter 3-15 characters (lowercase letters and numbers only)
- **Environment**: Select from Development, Test, Staging, or Production
- **Region**: Choose the Azure region closest to your users

### Step 2: App Service Configuration
- **App Service Plan SKU**: 
  - **Free F1**: Good for development and testing (limited)
  - **Basic B1**: Recommended for testing environments
  - **Standard S1**: Recommended for production environments
  - **Premium**: For high-performance scenarios
- **Application Insights**: Enable monitoring (recommended for production)
- **Backup**: Enable automated backups (requires Basic or higher SKU)

### Step 3: Azure Services Configuration
- **Communication Services**: Choose Free F0 for testing or Standard S0 for production
- **Speech Services**: Choose Free F0 for testing or Standard S0 for production

### Step 4: Optional Configuration
- **Agent Phone Number**: Enter in E.164 format (e.g., +1234567890) or leave empty
- **DirectLine Secret**: Enter your Bot Framework DirectLine secret or leave empty
- **Health Check API Key**: Auto-generated if not provided

## What Gets Created

The deployment creates these Azure resources with consistent naming:

| Resource Type | Example Name | Purpose |
|---------------|--------------|---------|
| Resource Group | `rg-agentvoicea1b2c3-prod` | Container for all resources |
| App Service Plan | `asp-agentvoicea1b2c3-prod` | Hosts your application |
| App Service | `app-agentvoicea1b2c3-prod` | Your ACS for MCS application |
| Key Vault | `kv-agentvoicea1b2c3-prod` | Stores configuration secrets |
| Communication Services | `acs-agentvoicea1b2c3-prod` | Handles voice calls |
| Speech Services | `cs-agentvoicea1b2c3-prod` | Voice recognition/synthesis |
| Application Insights | `ai-agentvoicea1b2c3-prod` | Monitoring (if enabled) |

## Post-Deployment Workflow

After successful ARM template deployment:

### 1. Download Release Package
```powershell
# Download from GitHub Releases
# https://github.com/holgerimbery/ACSforMCS/releases/latest
# Extract ACSforMCS-Release-Package.zip
```

### 2. Configure Missing Secrets (if any)
```powershell
# Navigate to extracted folder
cd C:\path\to\extracted\folder

# Configure phone number and DirectLine secret
.\setup-configuration.ps1
```

### 3. Deploy Application Code
```powershell
# Deploy the application to your new resources
.\deploy-application.ps1
```

### 4. Verify Deployment
```powershell
# Check configuration and test endpoints
.\show-environment.ps1
```

### 5. Configure Additional Components
For complete setup, follow these detailed guides in the [Wiki](https://github.com/holgerimbery/ACSforMCS/wiki):
- **[Phone Number Acquisition and Setup](https://github.com/holgerimbery/ACSforMCS/wiki/Prerequisites-and-Setup#3-azure-communication-services-acs)** - How to order and configure phone numbers
- **[Copilot Studio Configuration](https://github.com/holgerimbery/ACSforMCS/wiki/Prerequisites-and-Setup#5-microsoft-copilot-studio)** - Configure agents for call handling
- **[Azure Web App Deployment](https://github.com/holgerimbery/ACSforMCS/wiki/Azure-Web-App-Deployment)** - Complete deployment guide
- **[Environment Management Scripts](https://github.com/holgerimbery/ACSforMCS/wiki/Environment-Management-Scripts)** - Automation and scripting

## Deployment Outputs

The ARM template provides these useful outputs:

- **Web App URL**: Direct link to your deployed application
- **Key Vault Name**: For configuration management
- **Resource Names**: All created Azure resource names
- **Next Steps**: Clear instructions for completing setup

## Troubleshooting

### Deployment Issues
- **Name conflicts**: Template uses unique suffixes to prevent conflicts
- **Permission errors**: Ensure you have Contributor access to the subscription/resource group
- **Region availability**: Some SKUs may not be available in all regions
- **Key Vault purge protection**: Template automatically configures appropriate purge protection settings

### Post-Deployment Issues
- **App not starting**: Check Application Insights for errors
- **Configuration errors**: Use `show-environment.ps1` to validate settings
- **Phone number issues**: Follow the phone number acquisition guide in the wiki

## Cost Considerations

### Free Tier Resources (F0 SKUs)
- Communication Services F0: Limited minutes/messages
- Speech Services F0: Limited transactions
- App Service F1: Limited always-on time, custom domains

### Production Recommendations
- App Service: Standard S1 or higher
- Communication Services: S0 (pay-as-you-go)
- Speech Services: S0 (pay-as-you-go)
- Application Insights: Enabled for monitoring

## Security Features

- **HTTPS Only**: All App Services enforce HTTPS
- **Managed Identity**: App Service uses system-assigned managed identity
- **Key Vault Access**: Proper access policies for secret retrieval
- **Soft Delete**: Key Vault soft delete enabled for recovery

## Support

For assistance:
- Review [Wiki Documentation](https://github.com/holgerimbery/ACSforMCS/wiki)
- Check [GitHub Issues](https://github.com/holgerimbery/ACSforMCS/issues)
- Use the troubleshooting guides in the release package