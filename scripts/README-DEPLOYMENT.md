# ACS for MCS - Release Deployment Guide

This folder contains the pre-built deployment package for ACS for MCS.

## Quick Start

1. **Prerequisites**:
   - Azure CLI installed and authenticated (`az login`)
   - Azure Web App resource created
   - Key Vault configured with required secrets

2. **Validate readiness** (optional but recommended):
   ```powershell
   ..\scripts\validate-deployment.ps1
   ```

3. **Deploy**:
   ```powershell
   .\deploy-release.ps1
   ```

## Files in this package

- `ACSforMCS-vX.X.X.zip` - Pre-compiled application package
- `deploy-release.ps1` - Deployment script for releases (copied from scripts/)
- `README-DEPLOYMENT.md` - This file

## Before deployment

Make sure you have run the configuration setup:
```powershell
..\scripts\setup-configuration.ps1
```

## Verification

After deployment, verify the installation:
```powershell
..\scripts\show-environment.ps1
```

## Parameters

The deployment script supports several parameters:

```powershell
# Basic deployment
.\deploy-release.ps1

# Skip confirmation prompts
.\deploy-release.ps1 -Force

# Specify Key Vault and app name
.\deploy-release.ps1 -KeyVaultName "my-vault" -AppName "my-app"
```

## Troubleshooting

If deployment fails:

1. **Run validation**: `.\scripts\validate-deployment.ps1`
2. **Check authentication**: `az account show`
3. **Verify Key Vault**: Ensure all required secrets exist
4. **Check permissions**: Verify access to Key Vault and Web App
5. **Review logs**: Check Azure portal for detailed error messages

For comprehensive troubleshooting, see `docs/TROUBLESHOOTING.md`.

## Required Azure Resources

- Azure Communication Services resource
- Azure Web App (Windows, .NET 9.0)
- Azure Key Vault with required secrets
- Azure Cognitive Services (Speech)
- Microsoft Bot Framework bot with DirectLine channel

## Support

- [GitHub Issues](https://github.com/holgerimbery/ACSforMCS/issues)
- [Full Documentation](../docs/)
- [Configuration Guide](../docs/CONFIGURATION.md)