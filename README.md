# telephony channel for Copilot Studio via Azure Communication Services

## Overview

This project showcases a telephony integration between **Azure Communication Services (ACS)** and **Microsoft Copilot Studio (MCS)** virtual agents. 
The application creates a voice-based customer service experience by:

1. Accepting incoming phone calls through ACS
2. Converting speech to text using real-time transcription
3. Sending the transcribed content to a Copilot Studio agent via the Direct Line API
4. Transforming the agent's responses into spoken audio using SSML (Speech Synthesis Markup Language)
5. Delivering the synthesized speech back to the caller
6. Transfer the call to an external phone number
7. Monitoring endpoints (secured)
8. Swagger interface (secured when running in Development Mode, disable in Production Mode)


This solution provides an alternative communication channel for Copilot Studio agents.
enabling organizations to extend their conversational AI capabilities to traditional phone systems
while leveraging the natural language understanding and dialog management features of Microsoft Copilot Studio.


https://github.com/user-attachments/assets/c3f3c304-f743-4eb3-9f28-dd22338489c1

## Documentation of the Project
[Project Wiki](https://github.com/holgerimbery/ACSforMCS/wiki)

## Quick Start Deployment

### New Projects (Automated Setup)
For new projects without existing Azure resources, use our automated setup:

```powershell
# 1. Download and extract the latest release package
# 2. Create all Azure resources automatically
.\scripts\create-azure-resources.ps1 -ApplicationName "myproject"

# 3. Populate Key Vault secrets interactively  
.\scripts\populate-keyvault-secrets.ps1 -KeyVaultName "kv-myproject-prod"

# 4. Deploy the application
.\scripts\deploy-application.ps1
```

### Existing Azure Resources
If you already have Azure resources configured:

```powershell
# 1. Download and extract the latest release package
# 2. Configure application with existing resources
.\scripts\setup-configuration.ps1 -KeyVaultName "your-keyvault-name"

# 3. Deploy the application
.\scripts\deploy-application.ps1
```

**📋 Complete deployment guides:**
- **[Release Package Quick Reference](https://github.com/holgerimbery/ACSforMCS/wiki/Release-Package-Quick-Reference)** - Step-by-step commands for fast deployment
- **[Release Package Deployment Guide](https://github.com/holgerimbery/ACSforMCS/wiki/Release-Package-Deployment)** - Comprehensive guide with detailed explanations


## Credits and Acknowledgments
This project is based on and inspired by architectural samples and technical guidance provided by Microsoft. We extend our gratitude to the Microsoft engineering teams for their comprehensive documentation and sample code that served as the foundation for this integration. The approach showcased here leverages best practices recommended by Microsoft for connecting Azure Communication Services with conversational AI platforms like Copilot Studio. Special thanks to the Azure Communication Services and Microsoft Copilot Studio product teams for their excellent technical resources that made this implementation possible.

     
## Want to Contribute?
Helping hands are welcome to enhance this telephony integration capability. If you're interested in contributing, please reach out to us with your ideas and PRs. 

