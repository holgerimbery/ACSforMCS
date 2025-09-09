# Prerequisites and Setup

## Prerequisites Overview

Before deploying ACSforMCS, ensure you have the following Azure resources and development## Regional Considerations

### **Supported Regions**

Choose regions that support all required services:

| Region | ACS | Cognitive Services | Recommended |
|--------|-----|-------------------|-----------|
| **West Europe** | ✅ | ✅ | ⭐ Europe/GDPR |
| **East US** | ✅ | ✅ | ⭐ North America |
| **Southeast Asia** | ✅ | ✅ | ⭐ Asia Pacific |
| **Australia East** | ✅ | ✅ | Australia |
| **UK South** | ✅ | ✅ | United Kingdom | This comprehensive setup guide will walk you through each requirement with detailed instructions.

## Required Azure Resources

### 1. **Azure Subscription**
- **Active Azure account** with sufficient permissions
- **Resource creation rights** in your subscription
- **Cost management** awareness - review [Azure pricing calculator](https://azure.microsoft.com/pricing/calculator/)

**Getting Started:**
- If you don't have an Azure account: [Sign up for free Azure account](https://azure.microsoft.com/free/)
- Free account includes **$200 credit** for exploring services
- Many services offer **free tiers** suitable for development and testing

### 2. **Azure Communication Services (ACS)**

Azure Communication Services provides the telephony infrastructure for handling incoming calls.

**Setup Steps:**
1. **Create ACS Resource**
   - Navigate to [Azure Portal](https://portal.azure.com)
   - Search for "Communication Services" and create a new resource
   - Choose your subscription and resource group
   - Select a region (preferably same as other resources)

2. **Configure Identity and Access**
   - Enable **System-Assigned Managed Identity**
   - Note the **Connection String** from the Keys section
   - Configure access to Cognitive Services resource

3. **Provision Phone Number**
   - Go to "Phone numbers" in your ACS resource
   - Purchase a phone number in your desired region
   - Configure the number for **Voice calls**
   - Note the phone number in E.164 format (e.g., +1234567890)

**Detailed Guide**: [ACS Setup Documentation](https://docs.microsoft.com/azure/communication-services/quickstarts/create-communication-resource)

### 3. **Azure Cognitive Services**

Provides speech-to-text and text-to-speech capabilities for natural conversation.

**Setup Steps:**
1. **Create Multi-Service Account**
   - Search for "Cognitive Services" in Azure Portal
   - Create a **multi-service** resource (not single-service)
   - Choose same region as your ACS resource for optimal performance
   - Select appropriate pricing tier (F0 for development, S0 for production)

2. **Configure Identity**
   - Enable **System-Assigned Managed Identity**
   - Note the **Endpoint URL** from the resource overview
   - Ensure the endpoint supports Speech services

3. **Region Considerations**
   - Use regions that support both ACS and Speech services
   - Recommended regions: **West Europe**, **East US**, **Southeast Asia**

**Detailed Guide**: [Cognitive Services Setup](https://learn.microsoft.com/azure/cognitive-services/cognitive-services-apis-create-account)

### 4. **Azure Key Vault**

Securely stores configuration secrets and connection strings.

**Setup Steps:**
1. **Create Key Vault**
   ```bash
   # Example Azure CLI commands
   az keyvault create \
     --name "kv-your-resource-base-name" \
     --resource-group "rg-your-resource-base-name-prod" \
     --location "westeurope" \
     --enable-rbac-authorization true
   ```

2. **Configure Access Control**
   - Use **RBAC (Role-Based Access Control)** for modern security
   - Avoid legacy access policies when possible
   - Your identity needs **Key Vault Administrator** role for setup

3. **Required Secrets**
   Configure these secrets in your Key Vault:
   
   | Secret Name | Description | Example Value |
   |-------------|-------------|---------------|
   | `AcsConnectionString` | ACS connection string | `endpoint=https://acs-....communication.azure.com/;accesskey=...` |
   | `CognitiveServiceEndpoint` | Speech services endpoint | `https://cog-....cognitiveservices.azure.com/` |
   | `DirectLineSecret` | Bot Framework DirectLine key | `your-directline-secret-key` |
   | `AgentPhoneNumber` | Default transfer number | `+1234567890` |
   | `BaseUri-Development` | Development environment URL | `https://your-devtunnel-url` |
   | `BaseUri-Production` | Production environment URL | `https://app-....azurewebsites.net` |

### 5. **Microsoft Copilot Studio**

Your conversational AI agent that handles the actual conversations.

**Setup Steps:**
1. **Create Copilot Studio Agent**
   - Go to [Copilot Studio Portal](https://web.powerva.microsoft.com/)
   - Create a new agent or use an existing one
   - Design conversation topics for your use case

2. **Configure DirectLine Channel**
   - Navigate to **Settings** → **Security**
   - **Disable Authentication** (required for phone channel)
   - Go to **Channels** → **Web Channel Security**
   - Copy one of the **Secret Keys** for DirectLine access

3. **Design Voice-Friendly Content**
   - Create topics suitable for voice interaction
   - Use clear, conversational language
   - Implement transfer logic using the `TRANSFER:number:message` format
   - Test responses through the web interface first

**Pro Tip**: Design your bot topics with voice interaction in mind - shorter responses work better than long text blocks.

## Development Environment

### **Required Tools**

1. **.NET 9 SDK**
   - Download from [dotnet.microsoft.com](https://dotnet.microsoft.com/download)
   - Verify installation: `dotnet --version`

2. **Azure CLI**
   - Download from [Azure CLI documentation](https://docs.microsoft.com/cli/azure/install-azure-cli)
   - Sign in: `az login`
   - Verify access: `az account show`

3. **Development Tunneling (For Local Development)**
   - **Azure Dev Tunnels CLI**: [Installation guide](https://learn.microsoft.com/azure/developer/dev-tunnels/get-started)
   - Alternative: Visual Studio dev tunnels (built-in)
   - Required for local webhook testing

4. **Code Editor**
   - **Visual Studio 2022** (recommended) with Azure workload
   - **Visual Studio Code** with C# extension
   - **JetBrains Rider** (alternative)

### **Optional but Recommended**

1. **Git** for version control
2. **Azure Storage Explorer** for troubleshooting
3. **Application Insights** for monitoring
4. **Postman** or similar for API testing

## 🌍 Regional Considerations

### **Supported Regions**

Choose regions that support all required services:

| Region | ACS | Cognitive Services | Recommended |
|--------|-----|-------------------|-------------|
| **West Europe** | ✅ | ✅ | ⭐ Europe/GDPR |
| **East US** | ✅ | ✅ | ⭐ North America |
| **Southeast Asia** | ✅ | ✅ | ⭐ Asia Pacific |
| **Australia East** | ✅ | ✅ | Australia |
| **UK South** | ✅ | ✅ | United Kingdom |

### **Europe-Specific Configuration**

If deploying in Europe, the code includes Europe-specific DirectLine endpoint:
```csharp
// Constants.cs
public const string DirectLineBaseUrl = "https://europe.directline.botframework.com/v3/directline/";
```

For other regions, use the standard endpoint:
```csharp
public const string DirectLineBaseUrl = "https://directline.botframework.com/v3/directline/";
```

## Cost Planning

### **Estimated Monthly Costs** (USD, varies by region)

| Service | Development | Production |
|---------|-------------|------------|
| **Azure Communication Services** | $0-10 | $50-200 |
| **Cognitive Services (Speech)** | $0-20 | $100-500 |
| **App Service** | $0-15 | $50-200 |
| **Key Vault** | $1-3 | $3-10 |
| **Application Insights** | $0-5 | $10-50 |
| **Total Estimate** | **$1-53** | **$213-960** |

**Cost Optimization Tips:**
- Use **free tiers** during development
- Monitor usage with **Azure Cost Management**
- Set up **spending alerts** to avoid surprises
- Consider **reserved capacity** for production workloads

## Security Preparation

### **Identity and Access Management**

1. **Service Principals** (for CI/CD)
   - Create service principal for automated deployments
   - Assign minimal required permissions
   - Store credentials securely in GitHub Secrets

2. **Managed Identities** (for runtime)
   - Enable system-assigned managed identity on App Service
   - Use for Key Vault access without storing credentials
   - Configure RBAC roles appropriately

### **Network Security**

1. **Firewall Rules**
   - Configure any corporate firewalls for Azure endpoints
   - Ensure outbound HTTPS (443) and WebSocket connections
   - Allow Azure Communication Services IP ranges

2. **HTTPS Enforcement**
   - All endpoints must be HTTPS for production
   - Configure SSL certificates for custom domains
   - Enable HSTS headers for security

## Setup Verification Checklist

Before proceeding to deployment, verify:

### **Azure Resources**
- [ ] ACS resource created with phone number provisioned
- [ ] Cognitive Services multi-service resource created
- [ ] Key Vault created with RBAC enabled
- [ ] All secrets configured in Key Vault
- [ ] Managed identities enabled where required

### **Copilot Studio**
- [ ] Agent created and topics configured
- [ ] DirectLine channel enabled
- [ ] Authentication disabled for phone channel
- [ ] DirectLine secret obtained and stored

### **Development Environment**
- [ ] .NET 9 SDK installed and verified
- [ ] Azure CLI installed and authenticated
- [ ] Dev tunneling solution chosen and configured
- [ ] Code editor set up with Azure extensions

### **Networking and Security**
- [ ] Corporate firewall configured for Azure access
- [ ] HTTPS endpoints planned for production
- [ ] Security roles and permissions documented

## Next Steps

Once all prerequisites are complete:

1. **Review Architecture** - Understand the system design and components
2. **Quick Start** - Follow immediate deployment steps for testing
3. **Production Setup** - Use the [Azure Web App Deployment](Azure-Web-App-Deployment.md) guide

## Common Setup Issues

### **Permission Errors**
- Ensure you have **Contributor** role on the subscription
- Check that **RBAC is enabled** on Key Vault
- Verify **service principal permissions** for CI/CD

### **Regional Availability**
- Some Azure services may not be available in all regions
- Check [Azure products by region](https://azure.microsoft.com/global-infrastructure/services/)
- Consider using paired regions for disaster recovery

### **Quota Limits**
- New Azure subscriptions have default quotas
- You may need to request quota increases for production workloads
- Monitor usage to avoid hitting limits

---

**Prerequisites complete?** Ready to dive into the [Azure Web App Deployment](Azure-Web-App-Deployment.md) guide!