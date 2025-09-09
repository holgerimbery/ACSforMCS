# ACSforMCS Wiki - Telephony Channel for Copilot Studio

Welcome to the **ACSforMCS** (Azure Communication Services for Microsoft Copilot Studio) wiki! This comprehensive resource provides everything you need to understand, deploy, and maintain a voice-enabled telephony integration between Azure Communication Services and Microsoft Copilot Studio.

## What is ACSforMCS?

ACSforMCS is a telephony integration solution that enables **Microsoft Copilot Studio** virtual agents to handle phone calls through **Azure Communication Services**. The application creates a seamless voice-based customer service experience by converting speech to text, processing conversations through AI agents, and delivering natural speech responses back to callers.

## Key Features

- **Voice-enabled AI conversations** through traditional phone calls
- **Real-time speech-to-text** transcription using Azure Cognitive Services
- **Natural text-to-speech** conversion for bot responses with SSML support
- **Call transfer capabilities** to human agents or other phone numbers
- **Secure configuration management** with Azure Key Vault integration
- **Production-ready deployment** options for Azure Web Apps
- **Comprehensive monitoring** and health check capabilities

## Documentation Structure

### **Getting Started**
- [Overview and Features](Overview-and-Features.md) - Understand what ACSforMCS does and its capabilities
- [Prerequisites and Setup](Prerequisites-and-Setup.md) - Required Azure resources and initial configuration
- [Azure Web App Deployment](Azure-Web-App-Deployment.md) - Complete production deployment guide

### **Technical Documentation**
- [Code Documentation](Code-Documentation.md) - Detailed technical implementation guide

### **Available Documentation**
The following documentation pages are currently available in this wiki:
- [Home](Home.md) - This overview page
- [Overview and Features](Overview-and-Features.md) - Comprehensive feature overview
- [Prerequisites and Setup](Prerequisites-and-Setup.md) - Setup requirements and configuration
- [Azure Web App Deployment](Azure-Web-App-Deployment.md) - Production deployment guide
- [Code Documentation](Code-Documentation.md) - Technical implementation details

## Live Demo

Experience the solution in action:

**Audio Sample**: [Listen to a sample call interaction](../assets/call.m4a)

This demonstrates a complete call flow from initial connection through AI conversation to call completion.

## Architecture at a Glance

```
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   Phone Call    │───▶│  Azure Comm     │───▶│   Azure Web     │
│                 │    │  Services        │    │   App           │
└─────────────────┘    └──────────────────┘    └─────────────────┘
                              │                         │
                              ▼                         ▼
                       ┌──────────────────┐    ┌─────────────────┐
                       │   Event Grid     │    │   Key Vault     │
                       │   Subscription   │    │   (RBAC)        │
                       └──────────────────┘    └─────────────────┘
                                                        │
                                                        ▼
                                               ┌─────────────────┐
                                               │ Copilot Studio  │
                                               │ (DirectLine)    │
                                               └─────────────────┘
```

## Quick Navigation

### **I want to...**

| Goal | Go To |
|------|-------|
| **Understand the solution** | [Overview and Features](Overview-and-Features.md) |
| **Check prerequisites** | [Prerequisites and Setup](Prerequisites-and-Setup.md) |
| **Deploy to production** | [Azure Web App Deployment](Azure-Web-App-Deployment.md) |
| **Understand the code** | [Code Documentation](Code-Documentation.md) |

## External Resources

- **[Azure Communication Services Documentation](https://docs.microsoft.com/azure/communication-services/)**
- **[Microsoft Copilot Studio Documentation](https://docs.microsoft.com/power-virtual-agents/)**
- **[Bot Framework DirectLine API](https://docs.microsoft.com/azure/bot-service/rest-api/bot-framework-rest-direct-line-3-0-concepts)**
- **[Azure Key Vault Documentation](https://docs.microsoft.com/azure/key-vault/)**

## License and Contributing

This project is licensed under the MIT License. See the [LICENSE.md](../LICENSE.md) file for details.

**Want to contribute?** We welcome contributions! Please see our contributing guidelines and submit pull requests for any enhancements or bug fixes.

## Support

If you encounter issues or have questions:

1. **Check the [Azure Web App Deployment](Azure-Web-App-Deployment.md) debugging section**
2. **Review the [Code Documentation](Code-Documentation.md) troubleshooting section**
3. **Submit an issue on GitHub**
4. **Contact the development team**

---

**Ready to enable voice conversations for your AI agents? Start with the [Prerequisites and Setup](Prerequisites-and-Setup.md) guide!**