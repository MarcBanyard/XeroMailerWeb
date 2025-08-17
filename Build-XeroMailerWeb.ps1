# Copyright © 2025 Marc Banyard
#
# This file is part of XeroMailerWeb.
#
# XeroMailerWeb is free software: you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# XeroMailerWeb is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with XeroMailerWeb. If not, see <https://www.gnu.org/licenses/>.
#
# This project remains under full copyright by Marc Banyard.
# Redistribution must retain this notice and remain under GPL v3 or
# compatible licensing.
#
# Build script for XeroMailer Web Application
# This script builds the web application and provides testing instructions

param(
    [string]$Configuration = "Release",
    [string]$OutputPath = "Deploy"
)

Write-Host "Building XeroMailer Web Application..." -ForegroundColor Green
Write-Host "Configuration: $Configuration" -ForegroundColor Cyan

# Clean previous builds
Write-Host "`nCleaning previous builds..." -ForegroundColor Yellow
$cleanPaths = @(
    "XeroMailerWeb\bin",
    "XeroMailerWeb\obj",
    $OutputPath
)

foreach ($path in $cleanPaths) {
    if (Test-Path $path) {
        try {
            Remove-Item -Path $path -Recurse -Force -ErrorAction Stop
            Write-Host "  Cleaned: $path" -ForegroundColor Gray
        } catch {
            Write-Host "  Failed to clean: $path - $_" -ForegroundColor Red
        }
    } else {
        Write-Host "  Path does not exist (already clean): $path" -ForegroundColor DarkGray
    }
}

# Build the web application
Write-Host "`nBuilding XeroMailerWeb project..." -ForegroundColor Yellow
dotnet build "XeroMailerWeb\XeroMailerWeb.csproj" -c $Configuration

if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to build XeroMailerWeb project" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "`nBuild completed successfully!" -ForegroundColor Green

# Create output directory if it doesn't exist
if (-not (Test-Path $OutputPath)) {
    Write-Host "Creating output directory: $OutputPath" -ForegroundColor Cyan
    New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
}

# Publish for production
Write-Host "`nPublishing for production..." -ForegroundColor Yellow
dotnet publish "XeroMailerWeb\XeroMailerWeb.csproj" --configuration $Configuration --output $OutputPath --no-build --self-contained false --verbosity normal

if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to publish XeroMailerWeb project" -ForegroundColor Red
    exit $LASTEXITCODE
}

# Remove PDB files
$pdbFiles = Get-ChildItem $OutputPath -Recurse -Include "*.pdb" -ErrorAction SilentlyContinue
if ($pdbFiles) {
    $pdbFiles | Remove-Item -Force
    Write-Host "Removed $($pdbFiles.Count) PDB files" -ForegroundColor Green
}

Write-Host "`nPublish completed successfully!" -ForegroundColor Green

# Display next steps
Write-Host "`n" + "="*60 -ForegroundColor Cyan
Write-Host "NEXT STEPS:" -ForegroundColor Cyan
Write-Host "="*60 -ForegroundColor Cyan

Write-Host "`n1. Configure appsettings.json with your credentials:" -ForegroundColor White
Write-Host "   - Update Xero:XeroClientId and Xero:XeroClientSecret" -ForegroundColor Gray
Write-Host "   - Update Entra settings for Microsoft Graph API" -ForegroundColor Gray
Write-Host "   - Add Xero:WebhookKey for signature verification" -ForegroundColor Gray

Write-Host "`n2. Test the application locally:" -ForegroundColor White
Write-Host "   cd XeroMailerWeb" -ForegroundColor Gray
Write-Host "   dotnet run" -ForegroundColor Gray

Write-Host "`n3. Deploy to a public URL (e.g., Azure App Service):" -ForegroundColor White
Write-Host "   - Deploy the published files from XeroMailerWeb\bin\publish" -ForegroundColor Gray
Write-Host "   - Configure the webhook URL in Xero Developer Portal" -ForegroundColor Gray

Write-Host "`n4. Configure Xero webhook:" -ForegroundColor White
Write-Host "   - Event Category: INVOICE" -ForegroundColor Gray
Write-Host "   - Event Type: CREATE" -ForegroundColor Gray
Write-Host "   - Webhook URL: https://your-domain.com/api/webhook/xero" -ForegroundColor Gray

Write-Host "`n5. Test webhook endpoint:" -ForegroundColor White
Write-Host "   GET https://your-domain.com/api/webhook/health" -ForegroundColor Gray

Write-Host "`n" + "="*60 -ForegroundColor Cyan 