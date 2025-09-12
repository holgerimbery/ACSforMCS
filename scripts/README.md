# Environment Management Scripts

This folder contains PowerShell scripts for automated environment management and deployment.

## Documentation

Complete documentation for these scripts has been moved to the wiki:

**[Environment Management Scripts Wiki Page](../.wiki/Environment-Management-Scripts.md)**

## Quick Reference

- `show-environment.ps1` - Display current environment status
- `switch-to-development.ps1` - Switch to development environment
- `switch-to-production.ps1` - Switch to production environment  
- `deploy-application.ps1` - Build and deploy application

## Usage

```powershell
# Check current status
.\scripts\show-environment.ps1

# Switch environments
.\scripts\switch-to-development.ps1 -Force
.\scripts\switch-to-production.ps1 -Force

# Deploy application
.\scripts\deploy-application.ps1 -Force
```

For complete documentation, usage examples, and troubleshooting, see the [Environment Management Scripts](../.wiki/Environment-Management-Scripts.md) wiki page.