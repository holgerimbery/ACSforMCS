# Fix Key Vault Access Policy - Backup/Troubleshooting Tool
# 
# PURPOSE: This script is a backup solution for Key Vault access issues.
# Use this script when:
# 1. ARM deployment failed to set Key Vault access policies correctly
# 2. You need to add additional users to Key Vault access after deployment  
# 3. You're troubleshooting "unauthorized" errors when accessing Key Vault secrets
# 4. You deployed without providing Object ID and need to fix access manually
#
# NOTE: The ARM template (Deploy to Azure button) already requires Object ID,
# so this script should only be needed for troubleshooting or post-deployment changes.

param(
    [Parameter(Mandatory=$true)]
    [string]$KeyVaultName,
    
    [Parameter(Mandatory=$true)]
    [string]$UserObjectId
)

Write-Host "🔧 Fixing Key Vault access policy..." -ForegroundColor Yellow

# Get current user Object ID if not provided
if ([string]::IsNullOrEmpty($UserObjectId)) {
    Write-Host "❌ Error: UserObjectId parameter is required" -ForegroundColor Red
    Write-Host "You can find your Object ID in Azure Portal > Microsoft Entra ID > Users > [Your User] > Object ID"
    Write-Host "Or run: az ad signed-in-user show --query objectId -o tsv"
    exit 1
}

# Add access policy for your user account
Write-Host "Adding access policy to Key Vault: $KeyVaultName"
try {
    az keyvault set-policy `
        --name $KeyVaultName `
        --object-id $UserObjectId `
        --secret-permissions get list set delete `
        --output none
    
    Write-Host "✅ Successfully added access policy!" -ForegroundColor Green
    Write-Host "You should now be able to view secrets in the Azure Portal" -ForegroundColor Green
}
catch {
    Write-Host "❌ Error adding access policy: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Please ensure you have permission to modify Key Vault access policies" -ForegroundColor Yellow
    exit 1
}

Write-Host ""
Write-Host "🎯 Next steps:" -ForegroundColor Cyan
Write-Host "1. Go to Azure Portal > Key Vault > $KeyVaultName > Secrets"
Write-Host "2. You should now be able to view and manage secrets"
Write-Host "3. Continue with your application deployment"