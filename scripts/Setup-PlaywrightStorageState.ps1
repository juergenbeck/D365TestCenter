<#
.SYNOPSIS
    Creates a Playwright storage-state file for UI tests against Markant DEV (ADR-0006).

.DESCRIPTION
    Wrapper around `D365TestCenter.Cli ui-setup`. Opens a headed Chromium,
    waits for manual login (MFA if needed), persists the storage-state.

    DEV-only hard-guard is enforced inside the CLI. PROD/TEST/DATATEST URLs
    are refused (Markant access matrix).

.PARAMETER Org
    Dataverse org URL. Default: https://markant-dev.crm4.dynamics.com

.PARAMETER Output
    Output path for the storage-state JSON. Default: auth/markant-dev-juergen.json
    (relative to the CLI working dir or absolute).

.NOTES
    Pre-requisites:
      1. dotnet build must succeed (CLI project)
      2. `playwright install chromium` must have been run once
         (Path: backend/D365TestCenter.Cli/bin/Debug/net8.0/playwright.ps1 install chromium)

    Recommendation: log in via an Incognito/Private browser session inside
    the headed Chromium to avoid SSO token spillover to other tenants.
#>

param(
    [string]$Org = "https://markant-dev.crm4.dynamics.com",
    [string]$Output = "auth/markant-dev-juergen.json"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$cliProj = Join-Path $repoRoot "backend/D365TestCenter.Cli"

if (-not (Test-Path $cliProj)) {
    throw "CLI-Projekt nicht gefunden: $cliProj"
}

# Build pruefen
$dll = Join-Path $cliProj "bin/Debug/net8.0/D365TestCenter.Cli.dll"
if (-not (Test-Path $dll)) {
    Write-Host "Build-Output nicht gefunden. Baue jetzt..." -ForegroundColor Yellow
    Push-Location $cliProj
    try {
        dotnet build
        if ($LASTEXITCODE -ne 0) { throw "dotnet build fehlgeschlagen" }
    } finally { Pop-Location }
}

# Browser-Install pruefen (einmalig nach NuGet-Restore)
$playwrightPs1 = Join-Path $cliProj "bin/Debug/net8.0/playwright.ps1"
if (Test-Path $playwrightPs1) {
    Write-Host "Pruefe Chromium-Installation..." -ForegroundColor Cyan
    & $playwrightPs1 install chromium
}

Write-Host ""
Write-Host "==> ui-setup gegen $Org" -ForegroundColor Cyan
Write-Host "    Output: $Output" -ForegroundColor Gray
Write-Host ""

Push-Location $cliProj
try {
    dotnet $dll ui-setup --org $Org --output $Output
    $exitCode = $LASTEXITCODE
} finally { Pop-Location }

if ($exitCode -eq 0) {
    Write-Host ""
    Write-Host "Setup OK. Storage-State liegt unter: $cliProj/$Output" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "Setup fehlgeschlagen (Exit-Code: $exitCode)" -ForegroundColor Red
    exit $exitCode
}
