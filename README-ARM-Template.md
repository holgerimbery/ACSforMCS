# Deploy to Azure - ARM Template

This ARM template provides a one-click deployment solution for ACS for Microsoft Copilot Studio with an interactive Azure Portal interface.

## Quick Deploy

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fholgerimbery%2FACSforMCS%2Fmain%2Fazuredeploy.json/createUIDefinitionUri/https%3A%2F%2Fraw.githubusercontent.com%2Fholgerimbery%2FACSforMCS%2Fmain%2FcreateUiDefinition.json)

**⚠️ Important**: Before clicking "Deploy to Azure", you need to create a resource group first. The deployment interface will show you the exact resource group name to use based on your project name and environment selection.

### Pre-Deployment Steps
1. **Create Resource Group**: Use the naming convention `rg-{projectName}{6-chars}-{environment}`
   - Example: `rg-agentvoicea1b2c3-prod`
   - The deployment form will show you the exact name after you enter your project details
2. **Get your Azure AD Object ID** (optional but recommended for Key Vault access)
   - Azure Portal → Azure Active Directory → Users → [Your User] → Object ID
   - Or run: `az ad signed-in-user show --query objectId -o tsv`

## What This Template Deploys

This ARM template creates a complete ACS for MCS environment including:

### Azure Resources Created
- **App Service Plan** - Hosts your application
- **App Service** - Your ACS for MCS application
- **Key Vault** - Securely stores configuration secrets
- **Communication Services** - Handles voice calls and messaging
- **Speech Services** - Provides voice recognition and synthesis
- **Application Insights** (optional) - Monitoring and diagnostics
- **Log Analytics Workspace** (optional) - Log storage for Application Insights

### Interactive Configuration Options

The deployment interface provides user-friendly options for:

#### Basic Settings
- **Project Name**: Base name for all resources (3-15 characters, auto-generates unique suffix)
- **Environment**: Development, Test, Staging, or Production
- **Region**: Azure region selection with optimal locations

#### App Service Configuration
- **SKU Selection**: From Free F1 to Premium P3v3 with clear recommendations
  - Free F1: Development use
  - Basic B1: Recommended for testing
  - Standard S1: Recommended for production
  - Premium: High-performance scenarios
- **Application Insights**: Enable monitoring (recommended)
- **Backup**: Enable automated backups (Basic+ SKUs only)

#### Azure Services Configuration
- **Communication Services Pricing**: Free F0 or Standard S0
- **Speech Services Pricing**: Free F0 or Standard S0

#### Optional Settings
- **Agent Phone Number**: E.164 format phone number
- **DirectLine Secret**: Bot Framework DirectLine secret
- **Health Check API Key**: Auto-generated if not provided

## Post-Deployment Steps

After the ARM template deployment completes:

1. **Download Release Package**
   - Go to the [GitHub Releases page](https://github.com/holgerimbery/ACSforMCS/releases)
   - Download the latest `ACSforMCS-Release-Package.zip`
   - Extract to a local folder

2. **Complete Configuration**
   ```powershell
   # Navigate to extracted folder
   cd path\to\extracted\folder
   
   # Configure remaining secrets (if not set during deployment)
   .\setup-configuration.ps1
   ```

3. **Deploy Application Code**
   ```powershell
   # Deploy the application to your new Azure resources
   .\deploy-application.ps1
   ```

4. **Verify Deployment**
   ```powershell
   # Check configuration and test endpoints
   .\show-environment.ps1
   ```

5. **Configure Additional Components**
   See the [Wiki](https://github.com/holgerimbery/ACSforMCS/wiki) for detailed configuration guides:
   - [Phone number acquisition and setup](https://github.com/holgerimbery/ACSforMCS/wiki/Prerequisites-and-Setup#3-azure-communication-services-acs)
   - [Copilot Studio agent configuration](https://github.com/holgerimbery/ACSforMCS/wiki/Prerequisites-and-Setup#5-microsoft-copilot-studio)
   - [Complete deployment and environment management](https://github.com/holgerimbery/ACSforMCS/wiki/Azure-Web-App-Deployment)
   - [Environment management scripts](https://github.com/holgerimbery/ACSforMCS/wiki/Environment-Management-Scripts)

## Template Features

### Smart Naming Convention
- All resources use consistent naming: `{type}-{projectName}{random6chars}-{environment}`
- Example: `app-agentvoicea1b2c3-prod`
- Random 6-character suffix ensures global uniqueness automatically
- Resource group naming: `rg-{projectName}{random6chars}-{environment}`

### Automatic Secret Population
The template automatically configures these Key Vault secrets:
- `BaseUri-Production` or `BaseUri-Development` (based on environment)
- `AcsConnectionString` (from Communication Services)
- `CognitiveServiceEndpoint` (from Speech Services)
- `HealthCheckApiKey` (auto-generated GUID)
- `AgentPhoneNumber` (if provided during deployment)
- `DirectLineSecret` (if provided during deployment)

### Security Best Practices
- HTTPS-only App Service
- Key Vault access policies for App Service managed identity
- Soft delete enabled on Key Vault
- Public network access controlled appropriately

### Cost Optimization
- Default to Free/Basic tiers for cost-effective testing
- Clear upgrade paths to production-ready SKUs
- Optional Application Insights to control monitoring costs

## Troubleshooting

### Common Issues

**Deployment Fails with "Name already exists"**
- Resource names include unique suffix to prevent conflicts
- If still failing, try a different project name

**Can't access Key Vault secrets**
- Ensure App Service managed identity has proper access policies
- Check that soft delete hasn't blocked secret creation

**Application not starting**
- Check Application Insights for startup errors
- Verify all required secrets are populated in Key Vault
- Use `show-environment.ps1` script to validate configuration

### Support

For issues and questions:
- Check the [GitHub Issues](https://github.com/holgerimbery/ACSforMCS/issues)
- Review the [Wiki documentation](https://github.com/holgerimbery/ACSforMCS/wiki)
- Follow the troubleshooting guides in the release package

## Template Files

- `azuredeploy.json` - Main ARM template with all resource definitions
- `azuredeploy.parameters.json` - Default parameter values
- `createUiDefinition.json` - Interactive Azure Portal interface
- `metadata.json` - Template metadata for Azure Marketplace
- `README-ARM-Template.md` - This documentation file

## Compatibility

- **PowerShell**: 5.1+ (Windows PowerShell) or 7.0+ (PowerShell Core)
- **Azure CLI**: 2.0+ required for post-deployment scripts
- **.NET**: Application runs on .NET 9.0
- **Azure Subscription**: Any tier (Free, Pay-as-you-go, Enterprise)

## License

This project is licensed under the MIT License - see the [LICENSE.md](LICENSE.md) file for details.